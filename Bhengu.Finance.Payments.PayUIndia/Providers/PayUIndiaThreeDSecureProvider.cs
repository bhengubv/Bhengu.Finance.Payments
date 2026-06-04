// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayUIndia.Providers;

/// <summary>
/// PayU India 3-D Secure provider. Wraps PayU India's S2S 3DS flow over the <c>_payment</c>
/// endpoint with <c>txn_s2s_flow=4</c> so the issuer-hosted ACS URL comes back in the JSON
/// response instead of as a browser redirect.
/// </summary>
/// <remarks>
/// PayU India's S2S 3DS flow returns either an ACS redirect URL (challenge required) or a
/// frictionless authorisation in the same response shape. The merchant POSTs card-token +
/// SHA-512 hash + the <c>txn_s2s_flow</c> sentinel and PayU India answers with
/// <c>metaData.txnType="3DS"</c> + <c>postUri</c> + form fields the consumer renders.
/// <para>This SDK exposes that response as a <see cref="ThreeDSecureChallenge"/>. The
/// <see cref="ThreeDSecureCompletion"/> returned by the issuer is supplied back on
/// <c>PaymentRequest.ThreeDSecureCompletion</c> and PayU India settles automatically.</para>
/// </remarks>
public sealed class PayUIndiaThreeDSecureProvider : BhenguProviderBase, IThreeDSecureProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly PayUIndiaOptions _options;
    private readonly string _baseUrl;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.PayUIndia;

    /// <summary>Create a new PayU India 3DS provider bound to the supplied HTTP client and options.</summary>
    public PayUIndiaThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<PayUIndiaOptions> options,
        ILogger<PayUIndiaThreeDSecureProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Salt))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.Salt)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://test.payu.in")
            : (_options.BaseUrl ?? "https://secure.payu.in");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl.TrimEnd('/') + "/");
    }

    /// <inheritdoc />
    public Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);
        return RunOperationAsync("start_3ds_authentication", async () =>
        {
            var txnid = chargeIntent.Metadata?.GetValueOrDefault("txnid") ?? $"3ds-{Guid.NewGuid():N}";
            var amount = chargeIntent.Amount.ToString("F2", CultureInfo.InvariantCulture);
            var productinfo = chargeIntent.Description ?? "Bhengu PayU India 3DS";
            var firstname = chargeIntent.Metadata?.GetValueOrDefault("firstname") ?? "Customer";
            var email = chargeIntent.Metadata?.GetValueOrDefault("email") ?? "buyer@example.com";

            var hashInput = string.Join("|",
                _options.MerchantKey, txnid, amount, productinfo, firstname, email,
                "", "", "", "", "",
                "", "", "", "", "",
                _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["txnid"] = txnid,
                ["amount"] = amount,
                ["productinfo"] = productinfo,
                ["firstname"] = firstname,
                ["email"] = email,
                ["pg"] = chargeIntent.Metadata?.GetValueOrDefault("pg") ?? "CC",
                ["bankcode"] = chargeIntent.Metadata?.GetValueOrDefault("bankcode") ?? "VISA",
                ["ccnum"] = chargeIntent.Metadata?.GetValueOrDefault("ccnum") ?? string.Empty,
                ["ccname"] = chargeIntent.Metadata?.GetValueOrDefault("ccname") ?? firstname,
                ["ccvv"] = chargeIntent.Metadata?.GetValueOrDefault("ccvv") ?? string.Empty,
                ["ccexpmon"] = chargeIntent.Metadata?.GetValueOrDefault("ccexpmon") ?? string.Empty,
                ["ccexpyr"] = chargeIntent.Metadata?.GetValueOrDefault("ccexpyr") ?? string.Empty,
                ["surl"] = chargeIntent.Metadata?.GetValueOrDefault("surl") ?? _options.SuccessUrl,
                ["furl"] = chargeIntent.Metadata?.GetValueOrDefault("furl") ?? _options.FailureUrl,
                ["txn_s2s_flow"] = "4",
                ["hash"] = hash
            };

            var raw = await PostFormAsync("_payment", form, ct, "StartAuthentication").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndia3DSResponse>(raw, DeserializeOptions);

            Logger.LogInformation("PayU India 3DS challenge: txnid={Txnid} status={Status} authRequired={AuthRequired}",
                txnid, response?.Status, response?.MetaData?.TxnStatus);

            var status = (response?.MetaData?.TxnStatus ?? response?.Status ?? string.Empty).ToLowerInvariant();
            var threeDsStatus = status switch
            {
                "redirect" or "authrequired" or "auth_required" => ThreeDSecureStatus.ChallengeRequired,
                "success" or "captured" => ThreeDSecureStatus.Authenticated,
                "attempted" => ThreeDSecureStatus.Attempted,
                "frictionless" or "notrequired" or "not_required" => ThreeDSecureStatus.NotRequired,
                "failure" or "failed" or "rejected" => ThreeDSecureStatus.Failed,
                _ => ThreeDSecureStatus.ChallengeRequired
            };

            return new ThreeDSecureChallenge
            {
                Status = threeDsStatus,
                ChallengeReference = response?.MihPayId ?? txnid,
                RedirectUrl = response?.MetaData?.PostUri,
                ChallengePayload = response?.MetaData?.AcsTemplate,
                ProtocolVersion = response?.MetaData?.ThreeDsVersion ?? "2.2.0",
                DsTransactionId = response?.MetaData?.DsTransactionId
            };
        }, ct);
    }

    /// <inheritdoc />
    public Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeReference);
        return RunOperationAsync("get_3ds_challenge", async () =>
        {
            // PayU India "verify_payment" against the merchant_postservice endpoint returns the
            // current authentication status for a given mihpayid / txnid.
            const string command = "verify_payment";
            var hashInput = string.Join("|", _options.MerchantKey, command, challengeReference, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = challengeReference,
                ["hash"] = hash
            };

            var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "GetChallenge").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndia3DSResponse>(raw, DeserializeOptions);

            var status = (response?.Status ?? "pending").ToLowerInvariant();
            var threeDsStatus = status switch
            {
                "success" or "captured" => ThreeDSecureStatus.Authenticated,
                "pending" or "in_progress" => ThreeDSecureStatus.ChallengeRequired,
                "attempted" => ThreeDSecureStatus.Attempted,
                _ => ThreeDSecureStatus.Failed
            };

            return new ThreeDSecureChallenge
            {
                Status = threeDsStatus,
                ChallengeReference = challengeReference,
                ProtocolVersion = response?.MetaData?.ThreeDsVersion ?? "2.2.0",
                DsTransactionId = response?.MetaData?.DsTransactionId
            };
        }, ct);
    }

    private async Task<string> PostFormAsync(string path, IDictionary<string, string> form, CancellationToken ct, string operation)
    {
        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PayU India failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayU India {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // === PayU India 3DS response shape (internal) ===

    private sealed class PayUIndia3DSResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("mihPayId")] public string? MihPayId { get; set; }
        [JsonPropertyName("metaData")] public PayUIndia3DSMetadata? MetaData { get; set; }
    }

    private sealed class PayUIndia3DSMetadata
    {
        [JsonPropertyName("txnStatus")] public string? TxnStatus { get; set; }
        [JsonPropertyName("postUri")] public string? PostUri { get; set; }
        [JsonPropertyName("acsTemplate")] public string? AcsTemplate { get; set; }
        [JsonPropertyName("threeDsVersion")] public string? ThreeDsVersion { get; set; }
        [JsonPropertyName("dsTransactionId")] public string? DsTransactionId { get; set; }
    }
}
