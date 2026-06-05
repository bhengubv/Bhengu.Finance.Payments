// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.CMI.Providers;

/// <summary>
/// CMI (Centre Monetique Interbancaire / Morocco) 3D Secure payment gateway provider.
/// CMI is a redirect-only 3DS card gateway based on the Garanti BBVA POS XML protocol.
/// <see cref="ProcessPaymentAsync"/> returns a signed redirect URL on
/// <see cref="PaymentResponse.RedirectUrl"/> that the caller must navigate the payer to.
/// There is no payouts API — CMI does not implement <see cref="IPayoutProvider"/>.
/// </summary>
/// <remarks>
/// 3DS is MANDATORY on every CMI charge — there is no opt-out. Liability shift applies whenever
/// the issuer's <c>mdStatus</c> is 1 (full auth) or 2/3/4 (attempted, Visa-only shift). See
/// <see cref="CMIThreeDSecureProvider"/> for an explicit step-up API.
/// </remarks>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class CMIPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly CMIOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.CMI;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.ThreeDSecure |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public CMIPaymentProvider(
        HttpClient httpClient,
        IOptions<CMIOptions> options,
        ILogger<CMIPaymentProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CMIOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.StoreKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CMIOptions.StoreKey)} is required");

        CMIHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request), ct);
    }

    private Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request)
    {
        var orderId = string.IsNullOrWhiteSpace(request.PaymentMethodToken)
            ? $"cmi-{Guid.NewGuid():N}"
            : request.PaymentMethodToken;
        var amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : NormaliseCurrency(request.Currency);
        var email = request.Metadata?.GetValueOrDefault("email") ?? string.Empty;
        var billToName = request.Metadata?.GetValueOrDefault("BillToName") ?? string.Empty;
        var rnd = request.Metadata?.GetValueOrDefault("rnd") ?? DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientid"] = _options.ClientId,
            ["storetype"] = "3D_PAY_HOSTING",
            ["hashAlgorithm"] = "ver3",
            ["TranType"] = "PreAuth",
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

        var hash = ComputeRedirectHash(fields, _options.StoreKey);
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

        var redirectUrl = sb.ToString();

        Logger.LogInformation("CMI redirect URL built for oid={OrderId} amount={Amount} currency={Currency}",
            orderId, amount, currency);

        return Task.FromResult(new PaymentResponse
        {
            GatewayReference = orderId,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = redirectUrl
        });
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var total = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var xml = BuildCC5Request("Credit", request.GatewayReference, total);
        var body = await CMIHttpClient.SendFormAsync(_httpClient, Logger, "fim/api",
            new Dictionary<string, string> { ["DATA"] = xml }, "ProcessRefund", ct).ConfigureAwait(false);

        var parsed = ParseCC5Response(body);
        Logger.LogInformation("CMI refund for oid={OrderId} response={Response} procReturnCode={Code}",
            request.GatewayReference, parsed.Response, parsed.ProcReturnCode);

        var status = parsed.Response == "Approved" ? PaymentStatus.Refunded : PaymentStatus.Failed;

        return new RefundResponse
        {
            GatewayReference = request.GatewayReference,
            Amount = request.Amount,
            Status = status,
            ProcessedAt = DateTime.UtcNow,
            Message = parsed.Response
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.StoreKey))
        {
            Logger.LogWarning("CMI StoreKey not configured — callback hash verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        return RunWebhookVerify(() =>
        {
            try
            {
                // CMI uses raw SHA-512 (NOT HMAC-SHA-512) over payload+storeKey. Base64 wire format.
                var canonical = payload + _options.StoreKey;
                var computed = SHA512.HashData(Encoding.UTF8.GetBytes(canonical));
                var supplied = Convert.FromBase64String(signature);
                return supplied.Length == computed.Length
                    && CryptographicOperations.FixedTimeEquals(supplied, computed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "CMI callback hash verification raised");
                return false;
            }
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload)
    {
        try
        {
            var pairs = ParseFormUrlEncoded(payload);
            var oid = pairs.GetValueOrDefault("oid");
            var procReturnCode = pairs.GetValueOrDefault("ProcReturnCode") ?? pairs.GetValueOrDefault("procReturnCode");
            var response = pairs.GetValueOrDefault("Response") ?? pairs.GetValueOrDefault("response");
            var mdStatus = pairs.GetValueOrDefault("mdStatus");
            var amount = decimal.TryParse(pairs.GetValueOrDefault("amount"), NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ? amt : 0m;
            var currency = pairs.GetValueOrDefault("currency") ?? _options.Currency;

            Logger.LogInformation("Parsed CMI callback: oid={Oid} response={Response} procReturn={Proc} mdStatus={MdStatus}",
                oid, response, procReturnCode, mdStatus);

            if (string.IsNullOrEmpty(oid))
                return Task.FromResult<WebhookEvent?>(null);

            // Refund signal — TranType=Credit in callback indicates refund acknowledgement.
            var tranType = pairs.GetValueOrDefault("TranType") ?? pairs.GetValueOrDefault("tranType");
            if (string.Equals(tranType, "Credit", StringComparison.OrdinalIgnoreCase) && response?.Equals("Approved", StringComparison.OrdinalIgnoreCase) == true)
            {
                return Task.FromResult<WebhookEvent?>(new RefundSucceededEvent
                {
                    GatewayReference = oid,
                    Status = PaymentStatus.Refunded,
                    EventType = response,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = oid,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false,
                    RawPayload = pairs.AsReadOnly()
                });
            }

            var statusUpper = response?.ToUpperInvariant();
            return Task.FromResult<WebhookEvent?>((statusUpper, procReturnCode) switch
            {
                ("APPROVED", _) => new ChargeSucceededEvent
                {
                    GatewayReference = oid,
                    Status = PaymentStatus.Completed,
                    EventType = response,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    RawPayload = pairs.AsReadOnly()
                },
                ("DECLINED", _) or ("ERROR", _) => new ChargeFailedEvent
                {
                    GatewayReference = oid,
                    Status = PaymentStatus.Failed,
                    EventType = response,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = procReturnCode,
                    FailureMessage = pairs.GetValueOrDefault("ErrMsg"),
                    RawPayload = pairs.AsReadOnly()
                },
                _ when mdStatus == "1" => new ChargeSucceededEvent
                {
                    GatewayReference = oid,
                    Status = PaymentStatus.Completed,
                    EventType = response,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency
                },
                _ => null
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse CMI callback");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    internal static string ComputeRedirectHash(SortedDictionary<string, string> fields, string storeKey)
    {
        var sb = new StringBuilder();
        foreach (var kv in fields)
        {
            if (string.Equals(kv.Key, "hash", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "encoding", StringComparison.OrdinalIgnoreCase)) continue;
            var escaped = (kv.Value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
            sb.Append(escaped).Append('|');
        }
        var escapedKey = (storeKey ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
        sb.Append(escapedKey);
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(bytes);
    }

    private string BuildCC5Request(string type, string orderId, string total)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "ISO-8859-9", null),
            new XElement("CC5Request",
                new XElement("Name", _options.ApiUser),
                new XElement("Password", _options.ApiPassword),
                new XElement("ClientId", _options.ClientId),
                new XElement("OrderId", orderId),
                new XElement("Type", type),
                new XElement("Currency", _options.Currency),
                new XElement("Total", total)));
        return doc.ToString();
    }

    private (string? Response, string? ProcReturnCode, string? OrderId) ParseCC5Response(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var root = doc.Root;
            return (
                root?.Element("Response")?.Value,
                root?.Element("ProcReturnCode")?.Value,
                root?.Element("OrderId")?.Value
            );
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "CMI XML response could not be parsed: {Body}", body);
            return (null, null, null);
        }
    }

    private static Dictionary<string, string> ParseFormUrlEncoded(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(body)) return dict;
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            dict[key] = value;
        }
        return dict;
    }

    private static string NormaliseCurrency(string currency)
    {
        return currency.ToUpperInvariant() switch
        {
            "MAD" => "504",
            "USD" => "840",
            "EUR" => "978",
            "GBP" => "826",
            _ => currency
        };
    }
}

/// <summary>Helpers for Dictionary surface used across CMI's webhooks.</summary>
internal static class CMIDictExtensions
{
    /// <summary>Project to an IReadOnlyDictionary view without copying.</summary>
    public static IReadOnlyDictionary<string, string> AsReadOnly(this Dictionary<string, string> source) => source;
}
