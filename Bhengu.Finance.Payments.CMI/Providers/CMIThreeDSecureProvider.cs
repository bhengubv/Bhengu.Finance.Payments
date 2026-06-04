// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Core.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.CMI.Providers;

/// <summary>
/// CMI implementation of <see cref="IThreeDSecureProvider"/>. Backed by CMI's
/// <c>/fim/est3Dgate</c> hosted authentication page — CMI's 3DS surface IS its hosted page;
/// there is no decoupled "begin 3DS / complete charge" pair.
/// </summary>
/// <remarks>
/// <para><see cref="StartAuthenticationAsync"/> returns the signed ACSURL redirect (status
/// <see cref="ThreeDSecureStatus.ChallengeRequired"/>) — there is no frictionless path on CMI;
/// the issuer's <c>mdStatus</c> code is only known after the payer returns.</para>
/// <para><see cref="GetChallengeAsync"/> issues a CC5 <c>Inquiry</c> XML against
/// <c>/fim/api</c> to read back the latest known status for a previously-issued <c>oid</c>.</para>
/// </remarks>
public sealed class CMIThreeDSecureProvider : IThreeDSecureProvider
{
    private readonly HttpClient _httpClient;
    private readonly CMIOptions _options;
    private readonly ILogger<CMIThreeDSecureProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.CMI;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public CMIThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<CMIOptions> options,
        ILogger<CMIThreeDSecureProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CMIOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.StoreKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CMIOptions.StoreKey)} is required");

        CMIHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "3ds.start");

        var orderId = string.IsNullOrWhiteSpace(chargeIntent.PaymentMethodToken)
            ? $"cmi-3ds-{Guid.NewGuid():N}"
            : chargeIntent.PaymentMethodToken;
        var amount = chargeIntent.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var currency = string.IsNullOrWhiteSpace(chargeIntent.Currency) ? _options.Currency : NormaliseCurrency(chargeIntent.Currency);
        var email = chargeIntent.Metadata?.GetValueOrDefault("email") ?? string.Empty;
        var billToName = chargeIntent.Metadata?.GetValueOrDefault("BillToName") ?? string.Empty;
        var rnd = chargeIntent.Metadata?.GetValueOrDefault("rnd") ?? DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientid"] = _options.ClientId,
            ["storetype"] = "3D_PAY_HOSTING",
            ["hashAlgorithm"] = "ver3",
            ["TranType"] = "Auth",
            ["amount"] = amount,
            ["currency"] = currency,
            ["oid"] = orderId,
            ["okUrl"] = _options.OkUrl ?? string.Empty,
            ["failUrl"] = _options.FailUrl ?? string.Empty,
            ["lang"] = _options.Lang,
            ["callbackUrl"] = _options.CallbackUrl ?? string.Empty,
            ["refreshtime"] = "5",
            ["rnd"] = rnd,
            ["BillToName"] = billToName,
            ["email"] = email
        };

        var hash = CMIPaymentProvider.ComputeRedirectHash(fields, _options.StoreKey);
        fields["hash"] = hash;

        var baseUrl = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? (_options.UseSandbox ? CMIHttpClient.SandboxDefaultUrl : CMIHttpClient.LiveDefaultUrl);
        var sb = new StringBuilder();
        sb.Append(baseUrl).Append("/fim/est3Dgate?");
        var first = true;
        foreach (var kv in fields)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
        }

        _logger.LogInformation("CMI 3DS challenge started: oid={Oid} amount={Amount}", orderId, amount);

        return Task.FromResult(new ThreeDSecureChallenge
        {
            Status = ThreeDSecureStatus.ChallengeRequired,
            ChallengeReference = orderId,
            RedirectUrl = sb.ToString(),
            ProtocolVersion = "2.x"
        });
    }

    /// <inheritdoc/>
    public async Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "3ds.get");

        var xml = new XDocument(
            new XDeclaration("1.0", "ISO-8859-9", null),
            new XElement("CC5Request",
                new XElement("Name", _options.ApiUser),
                new XElement("Password", _options.ApiPassword),
                new XElement("ClientId", _options.ClientId),
                new XElement("OrderId", challengeReference),
                new XElement("Type", "Inquiry"),
                new XElement("Extra",
                    new XElement("ORDERHISTORY", "Query"))))
            .ToString();

        try
        {
            var body = await CMIHttpClient.SendFormAsync(_httpClient, _logger, "fim/api",
                new Dictionary<string, string> { ["DATA"] = xml }, "3ds.Inquire", ct).ConfigureAwait(false);

            var doc = XDocument.Parse(body);
            var response = doc.Root?.Element("Response")?.Value;
            var procReturnCode = doc.Root?.Element("ProcReturnCode")?.Value;
            var mdStatus = doc.Root?.Element("Extra")?.Element("mdStatus")?.Value
                ?? doc.Root?.Element("Extra")?.Element("MDSTATUS")?.Value;

            var status = (response?.ToUpperInvariant(), mdStatus) switch
            {
                ("APPROVED", "1") => ThreeDSecureStatus.Authenticated,
                ("APPROVED", _) => ThreeDSecureStatus.Authenticated,
                (_, "1") => ThreeDSecureStatus.Authenticated,
                (_, "2" or "3" or "4") => ThreeDSecureStatus.Attempted,
                ("DECLINED", _) or ("ERROR", _) => ThreeDSecureStatus.Failed,
                _ => ThreeDSecureStatus.ChallengeRequired
            };

            return new ThreeDSecureChallenge
            {
                Status = status,
                ChallengeReference = challengeReference,
                DsTransactionId = procReturnCode,
                ProtocolVersion = "2.x"
            };
        }
        catch (PaymentDeclinedException)
        {
            return new ThreeDSecureChallenge
            {
                Status = ThreeDSecureStatus.ChallengeRequired,
                ChallengeReference = challengeReference
            };
        }
    }

    private static string NormaliseCurrency(string currency) => currency.ToUpperInvariant() switch
    {
        "MAD" => "504",
        "USD" => "840",
        "EUR" => "978",
        "GBP" => "826",
        _ => currency
    };
}
