// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayJustNow.Providers;

/// <summary>
/// PayJustNow Buy-Now-Pay-Later (BNPL) provider. 3 x interest-free instalments for South African
/// consumers. PayJustNow does NOT support payouts — <see cref="IPayoutProvider"/> is intentionally
/// not implemented.
/// </summary>
public sealed class PayJustNowPaymentProvider : IPaymentGatewayProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly PayJustNowOptions _options;
    private readonly ILogger<PayJustNowPaymentProvider> _logger;

    public string ProviderName => "payjustnow";

    public PayJustNowPaymentProvider(
        HttpClient httpClient,
        IOptions<PayJustNowOptions> options,
        ILogger<PayJustNowPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://sandbox.payjustnow.com/v1/")
                : (_options.BaseUrl ?? "https://api.payjustnow.com/v1/"));
        }

        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Add("X-Merchant-Id", _options.MerchantId);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (int)(request.Amount * 100);

        var requestBody = new
        {
            merchant_id = _options.MerchantId,
            amount = amountInCents,
            currency = request.Currency.ToUpperInvariant(),
            description = request.Description,
            customer_token = request.PaymentMethodToken,
            merchant_reference = request.Metadata?.GetValueOrDefault("order_id") ?? Guid.NewGuid().ToString("N"),
            callback_url = request.Metadata?.GetValueOrDefault("callback_url") ?? string.Empty,
            instalment_count = 3,
            metadata = request.Metadata ?? new Dictionary<string, string>().AsReadOnly() as IReadOnlyDictionary<string, string>
        };

        var body = await SendAsync(HttpMethod.Post, "orders", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var pjnResponse = JsonSerializer.Deserialize<PjnOrderResponse>(body, DeserializeOptions);

        _logger.LogInformation("PayJustNow order created: {OrderId} status={Status}",
            pjnResponse?.OrderId, pjnResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = pjnResponse?.OrderId ?? string.Empty,
            Status = MapStatus(pjnResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = pjnResponse?.CheckoutUrl is { Length: > 0 } url
                ? $"BNPL plan created. Checkout URL: {url}"
                : "BNPL plan created"
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (int)(request.Amount * 100);
        var requestBody = new
        {
            order_id = request.GatewayReference,
            amount = amountInCents,
            reason = request.Reason
        };

        var body = await SendAsync(HttpMethod.Post, "refunds", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<PjnRefundResponse>(body, DeserializeOptions);

        _logger.LogInformation("PayJustNow refund created: {RefundId} for order {OrderId}",
            refundResponse?.RefundId, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.RefundId ?? string.Empty,
            Amount = request.Amount,
            Status = MapStatus(refundResponse?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            _logger.LogWarning("PayJustNow SecretKey not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.SecretKey));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayJustNow webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<PjnWebhookEvent>(payload, DeserializeOptions);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed PayJustNow webhook event: {EventType} for order {OrderId}",
                webhookEvent.EventType, webhookEvent.OrderId);

            var status = webhookEvent.EventType?.ToLowerInvariant() switch
            {
                "order.approved" or "order.completed" => PaymentStatus.Completed,
                "order.declined" or "order.cancelled" => PaymentStatus.Failed,
                "instalment.paid" => PaymentStatus.Pending,
                "instalment.overdue" or "instalment.failed" => PaymentStatus.Failed,
                "refund.approved" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(webhookEvent.OrderId))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhookEvent.OrderId,
                Status = status.Value,
                EventType = webhookEvent.EventType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse PayJustNow webhook event");
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

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PayJustNow failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayJustNow {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "approved" or "active" or "completed" => PaymentStatus.Completed,
        "pending" or "created" or "processing" => PaymentStatus.Pending,
        "declined" or "cancelled" or "canceled" or "expired" => PaymentStatus.Failed,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === PayJustNow API response shapes (internal) ===

    private sealed class PjnOrderResponse
    {
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("checkout_url")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("amount")] public int? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class PjnRefundResponse
    {
        [JsonPropertyName("refund_id")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class PjnWebhookEvent
    {
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("instalment_number")] public int? InstalmentNumber { get; set; }
    }
}
