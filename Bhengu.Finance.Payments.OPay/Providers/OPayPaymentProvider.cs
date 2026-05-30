// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.OPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OPay.Providers;

/// <summary>
/// OPay (Nigeria/Egypt/Pakistan) payment gateway provider. Wraps the OPay International
/// cashier, refund and payout REST APIs. Requests are HMAC-SHA512 signed with the merchant SecretKey
/// and the signature is carried in the <c>Authorization</c> header.
/// </summary>
public sealed class OPayPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private const string ProductionBaseUrl = "https://liveapi.opaycheckout.com";
    private const string SandboxBaseUrl = "https://sandboxapi.opaycheckout.com";

    private readonly HttpClient _httpClient;
    private readonly OPayOptions _options;
    private readonly ILogger<OPayPaymentProvider> _logger;

    public string ProviderName => "opay";

    public OPayPaymentProvider(
        HttpClient httpClient,
        IOptions<OPayOptions> options,
        ILogger<OPayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.PublicKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.PublicKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.SecretKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var baseUrl = _options.UseSandbox
                ? _options.SandboxUrl ?? SandboxBaseUrl
                : _options.BaseUrl ?? ProductionBaseUrl;
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = request.Metadata?.GetValueOrDefault("reference") ?? $"opay-{Guid.NewGuid():N}";
        var userId = request.Metadata?.GetValueOrDefault("userId") ?? "anonymous";
        var userEmail = request.Metadata?.GetValueOrDefault("userEmail") ?? "noreply@bhengu.example";
        var userMobile = request.Metadata?.GetValueOrDefault("userMobile") ?? "";
        var userName = request.Metadata?.GetValueOrDefault("userName") ?? "Bhengu Customer";

        var amountTotal = (long)(request.Amount * 100);
        var requestBody = new
        {
            publicKey = _options.PublicKey,
            country = _options.Country,
            reference,
            amount = new { total = amountTotal, currency = request.Currency.ToUpperInvariant() },
            returnUrl = _options.ReturnUrl,
            callbackUrl = _options.CallbackUrl,
            expireAt = 30,
            sn = _options.MerchantId,
            productList = new[]
            {
                new
                {
                    name = request.Description,
                    description = request.Description,
                    quantity = 1,
                    price = amountTotal,
                    currency = request.Currency.ToUpperInvariant()
                }
            },
            userInfo = new { userId, userEmail, userMobile, userName },
            payMethod = request.PaymentMethodToken
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/cashier/create",
            requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPayCashierData>>(body);

        _logger.LogInformation("OPay cashier created: {OrderNo} code={Code}",
            resp?.Data?.OrderNo, resp?.Code);

        return new PaymentResponse
        {
            GatewayReference = resp?.Data?.OrderNo ?? reference,
            Status = MapResponseCode(resp?.Code, resp?.Data?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amount = (long)(request.Amount * 100);
        var requestBody = new
        {
            publicKey = _options.PublicKey,
            country = _options.Country,
            reference = $"refund-{Guid.NewGuid():N}",
            orderNo = request.GatewayReference,
            refundAmount = new { total = amount, currency = "NGN" },
            reason = request.Reason,
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/refund/create",
            requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPayRefundData>>(body);

        _logger.LogInformation("OPay refund created: {RefundId} for order {OrderNo}",
            resp?.Data?.RefundId, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = resp?.Data?.RefundId ?? string.Empty,
            Amount = request.Amount,
            Status = MapResponseCode(resp?.Code, resp?.Data?.Status),
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountTotal = (long)(request.Amount * 100);
        var requestBody = new
        {
            publicKey = _options.PublicKey,
            country = _options.Country,
            reference = $"payout-{Guid.NewGuid():N}",
            amount = new { total = amountTotal, currency = request.Currency.ToUpperInvariant() },
            reason = request.Description,
            receiver = new { receiverId = request.DestinationToken },
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/international/payout/create",
            requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPayPayoutData>>(body);

        _logger.LogInformation("OPay payout created: {OrderNo} code={Code}",
            resp?.Data?.OrderNo, resp?.Code);

        return new PayoutResponse
        {
            GatewayReference = resp?.Data?.OrderNo ?? string.Empty,
            Status = MapResponseCode(resp?.Code, resp?.Data?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            _logger.LogWarning("OPay SecretKey not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.SecretKey));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            // OPay typically prefixes "Bearer " on the Authorization webhook header — strip if present.
            var normalised = signature.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? signature["Bearer ".Length..]
                : signature;

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(normalised.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OPay webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var evt = JsonSerializer.Deserialize<OPayWebhookEvent>(payload);
            if (evt is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed OPay webhook event: {EventType}", evt.Type);

            // OPay sends both "type" and (under payload) "status" — favour the explicit type.
            var status = evt.Type?.ToLowerInvariant() switch
            {
                "transaction.success" or "payment.success" => PaymentStatus.Completed,
                "transaction.failed" or "payment.failed" => PaymentStatus.Failed,
                "refund.success" or "refund.completed" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            // Fallback to embedded status field when no event-type mapping matched.
            status ??= evt.Payload?.Status?.ToLowerInvariant() switch
            {
                "success" or "successful" => PaymentStatus.Completed,
                "failed" or "fail" => PaymentStatus.Failed,
                "refunded" => PaymentStatus.Refunded,
                _ => null
            };

            if (status is null || string.IsNullOrEmpty(evt.Payload?.Reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.Payload.Reference,
                Status = status.Value,
                EventType = evt.Type
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse OPay webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // HMAC-SHA512 of the request body using SecretKey, lowercase hex, sent as Authorization: Bearer <sig>
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.SecretKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signature);
        req.Headers.TryAddWithoutValidation("MerchantId", _options.MerchantId);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to OPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapResponseCode(string? code, string? status)
    {
        // OPay envelope code "00000" means success; sub-status carries lifecycle detail.
        if (string.Equals(code, "00000", StringComparison.Ordinal))
        {
            return status?.ToLowerInvariant() switch
            {
                "success" or "successful" => PaymentStatus.Completed,
                "initial" or "pending" or "processing" => PaymentStatus.Pending,
                "failed" or "fail" => PaymentStatus.Failed,
                "close" or "closed" or "cancelled" or "canceled" => PaymentStatus.Cancelled,
                "refunded" => PaymentStatus.Refunded,
                _ => PaymentStatus.Pending
            };
        }
        return PaymentStatus.Failed;
    }

    // === OPay API response shapes (internal) ===

    private sealed class OPayResponse<T> where T : class
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class OPayCashierData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("cashierUrl")] public string? CashierUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public OPayAmount? Amount { get; set; }
    }

    private sealed class OPayRefundData
    {
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class OPayPayoutData
    {
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class OPayAmount
    {
        [JsonPropertyName("total")] public long Total { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class OPayWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("payload")] public OPayWebhookPayload? Payload { get; set; }
    }

    private sealed class OPayWebhookPayload
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
