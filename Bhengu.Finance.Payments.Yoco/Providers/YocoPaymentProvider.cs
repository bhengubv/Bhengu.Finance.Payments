// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco (South Africa) payment gateway provider. Wraps the Yoco Online REST API.
/// Yoco does NOT expose payouts on the standard merchant API — <see cref="IPayoutProvider"/>
/// is intentionally not implemented; merchants requiring payouts should use Yoco Business/Marketplace.
/// </summary>
public sealed class YocoPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;
    private readonly ILogger<YocoPaymentProvider> _logger;

    public string ProviderName => ProviderNames.Yoco;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.SyncSettlement |
        ProviderCapabilities.Cards;

    public YocoPaymentProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (int)(request.Amount * 100);

        var requestBody = new
        {
            token = request.PaymentMethodToken,
            amountInCents,
            currency = request.Currency.ToUpperInvariant(),
            metadata = request.Metadata ?? new Dictionary<string, string>().AsReadOnly() as IReadOnlyDictionary<string, string>
        };

        var (body, _) = await SendAsync(HttpMethod.Post, "charges/", requestBody, ct, "ProcessPayment").ConfigureAwait(false);

        var yocoResponse = JsonSerializer.Deserialize<YocoChargeResponse>(body);

        _logger.LogInformation("Yoco charge created: {ChargeId} status={Status}", yocoResponse?.Id, yocoResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = yocoResponse?.Id ?? string.Empty,
            Status = MapStatus(yocoResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = yocoResponse?.Status
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (int)(request.Amount * 100);
        var requestBody = new
        {
            chargeId = request.GatewayReference,
            amountInCents
        };

        var (body, _) = await SendAsync(HttpMethod.Post, "refunds/", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<YocoRefundResponse>(body);

        _logger.LogInformation("Yoco refund created: {RefundId} for charge {ChargeId}",
            refundResponse?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Id ?? string.Empty,
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

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Yoco WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToBase64String(computedHash);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Yoco webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<YocoWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Yoco webhook event: {EventType}", webhookEvent.Type);

            var status = webhookEvent.Type?.ToLowerInvariant() switch
            {
                "payment.succeeded" => PaymentStatus.Completed,
                "payment.failed" => PaymentStatus.Failed,
                "refund.succeeded" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(webhookEvent.Payload?.Id))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhookEvent.Payload.Id,
                Status = status.Value,
                EventType = webhookEvent.Type
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Yoco webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<(string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Yoco failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return (responseBody, response);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "successful" or "succeeded" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Yoco API response shapes (internal) ===

    private sealed class YocoChargeResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amountInCents")] public int AmountInCents { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class YocoRefundResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("chargeId")] public string? ChargeId { get; set; }
        [JsonPropertyName("amountInCents")] public int AmountInCents { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class YocoWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("payload")] public YocoChargeResponse? Payload { get; set; }
    }
}
