// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ChipperCash.Providers;

/// <summary>
/// Chipper Cash (pan-African) payment gateway provider. Wraps the Chipper Cash REST API
/// for mobile-money collections, disbursements and refunds, with HMAC-SHA256 request signing
/// and webhook verification.
/// </summary>
public sealed class ChipperCashPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly ChipperCashOptions _options;
    private readonly ILogger<ChipperCashPaymentProvider> _logger;

    public string ProviderName => ProviderNames.ChipperCash;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.CrossBorder;

    public ChipperCashPaymentProvider(
        HttpClient httpClient,
        IOptions<ChipperCashOptions> options,
        ILogger<ChipperCashPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiSecret)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.chippercash.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(_options.ApiKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = request.Metadata?.GetValueOrDefault("reference") ?? $"chp-{Guid.NewGuid():N}";
        var msisdn = request.Metadata?.GetValueOrDefault("msisdn") ?? request.PaymentMethodToken;
        var network = request.Metadata?.GetValueOrDefault("network") ?? "MTN";
        var country = request.Metadata?.GetValueOrDefault("country") ?? _options.Country;

        var requestBody = new
        {
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            country,
            mobile = new { msisdn, network },
            reference,
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "v1/collections", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<ChipperCashCollectionResponse>(body);

        _logger.LogInformation("Chipper collection created: {Id} status={Status}",
            resp?.Id, resp?.Status);

        return new PaymentResponse
        {
            GatewayReference = resp?.Id ?? reference,
            Status = MapStatus(resp?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = new { amount = request.Amount, reason = request.Reason };
        var path = $"v1/collections/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<ChipperCashCollectionResponse>(body);

        _logger.LogInformation("Chipper refund created: {Id} for {OriginalRef}",
            resp?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = resp?.Id ?? string.Empty,
            Amount = request.Amount,
            Status = MapStatus(resp?.Status),
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var msisdn = request.DestinationToken;
        var requestBody = new
        {
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            destination = new
            {
                country = _options.Country,
                mobile = new { msisdn },
                name = "Bhengu Beneficiary",
                email = "noreply@bhengu.example"
            },
            reference = $"chp-payout-{Guid.NewGuid():N}",
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "v1/disbursements", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<ChipperCashCollectionResponse>(body);

        _logger.LogInformation("Chipper disbursement created: {Id} status={Status}", resp?.Id, resp?.Status);

        return new PayoutResponse
        {
            GatewayReference = resp?.Id ?? string.Empty,
            Status = MapStatus(resp?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
        {
            _logger.LogWarning("Chipper ApiSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chipper webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var evt = JsonSerializer.Deserialize<ChipperCashWebhookEvent>(payload);
            if (evt is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Chipper webhook event: {EventType}", evt.Event);

            var status = evt.Event?.ToLowerInvariant() switch
            {
                "collection.successful" or "disbursement.successful" or "payment.successful"
                    => PaymentStatus.Completed,
                "collection.failed" or "disbursement.failed" or "payment.failed"
                    => PaymentStatus.Failed,
                "refund.successful" or "refund.processed"
                    => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(evt.Data?.Id))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.Data.Id,
                Status = status.Value,
                EventType = evt.Event
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Chipper webhook event");
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

        // HMAC-SHA256 of "<body>.<timestamp>" using ApiSecret, lowercase hex, in X-Chipper-Signature.
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiSecret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json + "." + timestamp))).ToLowerInvariant();
        req.Headers.TryAddWithoutValidation("X-Chipper-Signature", signature);
        req.Headers.TryAddWithoutValidation("X-Chipper-Timestamp", timestamp);
        if (!string.IsNullOrWhiteSpace(_options.MerchantId))
            req.Headers.TryAddWithoutValidation("X-Chipper-Merchant-Id", _options.MerchantId);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Chipper Cash failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Chipper {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "successful" or "success" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" or "refund_processed" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Chipper Cash API response shapes (internal) ===

    private sealed class ChipperCashCollectionResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }

    private sealed class ChipperCashWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public ChipperCashWebhookData? Data { get; set; }
    }

    private sealed class ChipperCashWebhookData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }
}
