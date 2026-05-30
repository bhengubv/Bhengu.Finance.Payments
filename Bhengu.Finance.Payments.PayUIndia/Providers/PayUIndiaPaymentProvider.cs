// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayUIndia.Providers;

/// <summary>
/// PayU India payment gateway provider. Wraps the PayU India REST + form-redirect API
/// and supports payments (hosted-page redirect), refunds, and PayU India payouts.
/// </summary>
/// <remarks>
/// PayU India's <c>_payment</c> endpoint is a browser-redirect, form-encoded POST. For
/// <see cref="IPaymentGatewayProvider.ProcessPaymentAsync"/> the SDK does NOT itself drive
/// the redirect — it builds the form fields + SHA-512 hash and returns a fully-qualified
/// redirect URL in <see cref="PaymentResponse.Message"/>. Callers POST that URL from the
/// browser. <c>GatewayReference</c> is the merchant-supplied <c>txnid</c>.
/// Refunds and payouts use the info-service JSON endpoint.
/// </remarks>
public sealed class PayUIndiaPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly PayUIndiaOptions _options;
    private readonly ILogger<PayUIndiaPaymentProvider> _logger;

    public string ProviderName => "payuindia";

    public PayUIndiaPaymentProvider(
        HttpClient httpClient,
        IOptions<PayUIndiaOptions> options,
        ILogger<PayUIndiaPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Salt))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.Salt)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.InfoBaseUrl ?? "https://info.payu.in/");
        }
    }

    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var txnid = request.Metadata?.GetValueOrDefault("txnid") ?? $"txn-{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var productinfo = request.Description ?? "Bhengu PayU India payment";
        var firstname = request.Metadata?.GetValueOrDefault("firstname") ?? "Customer";
        var email = request.Metadata?.GetValueOrDefault("email") ?? "buyer@example.com";
        var phone = request.Metadata?.GetValueOrDefault("phone") ?? string.Empty;
        var udf1 = request.Metadata?.GetValueOrDefault("udf1") ?? string.Empty;
        var udf2 = request.Metadata?.GetValueOrDefault("udf2") ?? string.Empty;
        var udf3 = request.Metadata?.GetValueOrDefault("udf3") ?? string.Empty;
        var udf4 = request.Metadata?.GetValueOrDefault("udf4") ?? string.Empty;
        var udf5 = request.Metadata?.GetValueOrDefault("udf5") ?? string.Empty;

        // hash = SHA-512(key|txnid|amount|productinfo|firstname|email|udf1|udf2|udf3|udf4|udf5|||||salt)
        var hashInput = string.Join("|",
            _options.MerchantKey, txnid, amount, productinfo, firstname, email,
            udf1, udf2, udf3, udf4, udf5,
            "", "", "", "", "",
            _options.Salt);
        var hash = Sha512Hex(hashInput);

        var baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://test.payu.in")
            : (_options.BaseUrl ?? "https://secure.payu.in");

        var fields = new Dictionary<string, string>
        {
            ["key"] = _options.MerchantKey,
            ["txnid"] = txnid,
            ["amount"] = amount,
            ["productinfo"] = productinfo,
            ["firstname"] = firstname,
            ["email"] = email,
            ["phone"] = phone,
            ["surl"] = request.Metadata?.GetValueOrDefault("surl") ?? _options.SuccessUrl,
            ["furl"] = request.Metadata?.GetValueOrDefault("furl") ?? _options.FailureUrl,
            ["udf1"] = udf1,
            ["udf2"] = udf2,
            ["udf3"] = udf3,
            ["udf4"] = udf4,
            ["udf5"] = udf5,
            ["hash"] = hash
        };

        var query = string.Join("&", fields.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var redirectUrl = $"{baseUrl.TrimEnd('/')}/_payment?{query}";

        _logger.LogInformation("PayU India payment initiated: txnid={Txnid} (redirect URL built)", txnid);

        return Task.FromResult(new PaymentResponse
        {
            GatewayReference = txnid,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant(),
            ProcessedAt = DateTime.UtcNow,
            Message = $"Redirect to: {redirectUrl}"
        });
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        const string command = "cancel_refund_transaction";
        var paymentId = request.GatewayReference;
        var tokenId = $"refund-{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

        // hash = SHA-512(key|command|var1|salt) — var1 = paymentId
        var hashInput = string.Join("|", _options.MerchantKey, command, paymentId, _options.Salt);
        var hash = Sha512Hex(hashInput);

        var form = new Dictionary<string, string>
        {
            ["key"] = _options.MerchantKey,
            ["command"] = command,
            ["var1"] = paymentId,
            ["var2"] = tokenId,
            ["var3"] = amount,
            ["hash"] = hash
        };

        var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = TryParseInfoResponse<PayUIndiaInfoResponse>(raw);

        _logger.LogInformation("PayU India refund created for payment {PaymentId} token {TokenId} status={Status}",
            paymentId, tokenId, refund?.Status);

        return new RefundResponse
        {
            GatewayReference = tokenId,
            Amount = request.Amount,
            Status = MapStatus(refund?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Msg ?? refund?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        const string command = "fund_transfer";
        var var1 = request.DestinationToken;
        var var2 = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var var3 = request.Description ?? "Bhengu payout";

        // hash = SHA-512(key|command|var1|salt)
        var hashInput = string.Join("|", _options.MerchantKey, command, var1, _options.Salt);
        var hash = Sha512Hex(hashInput);

        var form = new Dictionary<string, string>
        {
            ["key"] = _options.MerchantKey,
            ["command"] = command,
            ["var1"] = var1,
            ["var2"] = var2,
            ["var3"] = var3,
            ["hash"] = hash
        };

        var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "ProcessPayout").ConfigureAwait(false);
        var payout = TryParseInfoResponse<PayUIndiaInfoResponse>(raw);

        _logger.LogInformation("PayU India payout initiated to {Destination} status={Status}", var1, payout?.Status);

        return new PayoutResponse
        {
            GatewayReference = payout?.Mihpayid ?? $"payout-{Guid.NewGuid():N}",
            Status = MapStatus(payout?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant(),
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.Salt))
        {
            _logger.LogWarning("PayU India Salt not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            // PayU India S2S response hash format:
            // hash = SHA-512(salt|status|||||udf5|udf4|udf3|udf2|udf1|email|firstname|productinfo|amount|txnid|key)
            // For simplicity, we expect callers to pass the recomputed hash input as `payload`.
            // Production callers should call VerifyWebhookSignature(reconstructedHashInput, response.hash).
            var computedHash = Sha512Hex(payload);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedHash));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayU India webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            // PayU India S2S callbacks are typically application/x-www-form-urlencoded.
            // We handle both URL-encoded form bodies and JSON for forward compatibility.
            PayUIndiaWebhookPayload? webhookEvent = null;

            var trimmed = payload.TrimStart();
            if (trimmed.StartsWith('{'))
            {
                webhookEvent = JsonSerializer.Deserialize<PayUIndiaWebhookPayload>(payload, DeserializeOptions);
            }
            else
            {
                webhookEvent = ParseFormUrlEncoded(payload);
            }

            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed PayU India webhook: status={Status} txnid={Txnid}",
                webhookEvent.Status, webhookEvent.Txnid);

            if (string.IsNullOrEmpty(webhookEvent.Txnid))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhookEvent.Txnid,
                Status = MapStatus(webhookEvent.Status ?? "pending"),
                EventType = $"payuindia.{webhookEvent.Status?.ToLowerInvariant() ?? "unknown"}"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse PayU India webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
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
            _logger.LogError("PayU India {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static T? TryParseInfoResponse<T>(string raw) where T : class
    {
        try
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
                return JsonSerializer.Deserialize<T>(raw, DeserializeOptions);
        }
        catch (JsonException)
        {
            // PayU info endpoint occasionally returns text on edge cases — swallow and let
            // callers see the raw status. Returning null is the contract for "couldn't parse".
        }
        return null;
    }

    private static PayUIndiaWebhookPayload ParseFormUrlEncoded(string payload)
    {
        var dict = payload.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(kv => kv.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1]));

        return new PayUIndiaWebhookPayload
        {
            Status = dict.GetValueOrDefault("status"),
            Txnid = dict.GetValueOrDefault("txnid"),
            Mihpayid = dict.GetValueOrDefault("mihpayid"),
            Amount = dict.GetValueOrDefault("amount"),
            Hash = dict.GetValueOrDefault("hash")
        };
    }

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "captured" or "completed" => PaymentStatus.Completed,
        "pending" or "in progress" or "in_progress" or "initiated" => PaymentStatus.Pending,
        "failed" or "failure" or "error" or "dropped" or "bounced" => PaymentStatus.Failed,
        "cancelled" or "canceled" or "user_cancelled" => PaymentStatus.Cancelled,
        "refunded" or "queued_for_refund" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === PayU India info-API response shapes (internal) ===

    private sealed class PayUIndiaInfoResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("mihpayid")] public string? Mihpayid { get; set; }
        [JsonPropertyName("request_id")] public string? RequestId { get; set; }
    }

    private sealed class PayUIndiaWebhookPayload
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("txnid")] public string? Txnid { get; set; }
        [JsonPropertyName("mihpayid")] public string? Mihpayid { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
    }
}
