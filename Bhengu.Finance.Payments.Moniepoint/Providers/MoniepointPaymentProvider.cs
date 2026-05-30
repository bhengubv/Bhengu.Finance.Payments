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
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Moniepoint.Providers;

/// <summary>
/// Moniepoint (Nigeria) payment gateway provider. Wraps the Moniepoint REST API
/// for initialised checkout, verify, refund and transfer operations.
/// </summary>
public sealed class MoniepointPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly MoniepointOptions _options;
    private readonly ILogger<MoniepointPaymentProvider> _logger;

    public string ProviderName => "moniepoint";

    public MoniepointPaymentProvider(
        HttpClient httpClient,
        IOptions<MoniepointOptions> options,
        ILogger<MoniepointPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MoniepointOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.moniepoint.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reference = request.Metadata?.GetValueOrDefault("reference") ?? $"mpt-{Guid.NewGuid():N}";
        var customerEmail = request.Metadata?.GetValueOrDefault("email") ?? "noreply@bhengu.example";
        var customerName = request.Metadata?.GetValueOrDefault("name") ?? "Bhengu Customer";
        var customerPhone = request.Metadata?.GetValueOrDefault("phone") ?? "";

        var requestBody = new
        {
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            reference,
            customer = new { email = customerEmail, name = customerName, phone = customerPhone },
            redirectUrl = _options.RedirectUrl,
            paymentMethod = request.PaymentMethodToken
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/transactions/initialize",
            requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointInitResponse>(body);

        _logger.LogInformation("Moniepoint init: {Reference} status={Status}",
            resp?.Data?.Reference ?? reference, resp?.Data?.Status);

        return new PaymentResponse
        {
            GatewayReference = resp?.Data?.Reference ?? reference,
            Status = MapStatus(resp?.Data?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = new
        {
            amount = request.Amount,
            reason = request.Reason
        };

        var path = $"api/v1/transactions/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointRefundResponse>(body);

        _logger.LogInformation("Moniepoint refund created: {RefundRef} for txn {TxnRef}",
            resp?.Data?.RefundReference, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = resp?.Data?.RefundReference ?? string.Empty,
            Amount = request.Amount,
            Status = MapStatus(resp?.Data?.Status),
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken expected format: "<bankCode>:<accountNumber>" or "<accountNumber>".
        string beneficiaryBank = string.Empty;
        string beneficiaryAccount = request.DestinationToken;
        var sep = request.DestinationToken.IndexOf(':');
        if (sep > 0)
        {
            beneficiaryBank = request.DestinationToken[..sep];
            beneficiaryAccount = request.DestinationToken[(sep + 1)..];
        }

        var requestBody = new
        {
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            beneficiaryAccount,
            beneficiaryBank,
            narration = request.Description,
            reference = $"tfr-{Guid.NewGuid():N}"
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/transfers", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointTransferResponse>(body);

        _logger.LogInformation("Moniepoint transfer created: {Reference} status={Status}",
            resp?.Data?.Reference, resp?.Data?.Status);

        return new PayoutResponse
        {
            GatewayReference = resp?.Data?.Reference ?? string.Empty,
            Status = MapStatus(resp?.Data?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.ApiKey : _options.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Moniepoint webhook secret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moniepoint webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var evt = JsonSerializer.Deserialize<MoniepointWebhookEvent>(payload);
            if (evt is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Moniepoint webhook event: {EventType}", evt.Event);

            var status = evt.Event?.ToLowerInvariant() switch
            {
                "transaction.successful" or "transfer.successful" => PaymentStatus.Completed,
                "transaction.failed" or "transfer.failed" => PaymentStatus.Failed,
                "refund.successful" or "refund.processed" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(evt.Data?.Reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.Data.Reference,
                Status = status.Value,
                EventType = evt.Event
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Moniepoint webhook event");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Moniepoint failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Moniepoint {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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

    // === Moniepoint API response shapes (internal) ===

    private sealed class MoniepointInitResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public MoniepointInitData? Data { get; set; }
    }

    private sealed class MoniepointInitData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("checkoutUrl")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class MoniepointRefundResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public MoniepointRefundData? Data { get; set; }
    }

    private sealed class MoniepointRefundData
    {
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class MoniepointTransferResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public MoniepointTransferData? Data { get; set; }
    }

    private sealed class MoniepointTransferData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class MoniepointWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public MoniepointWebhookData? Data { get; set; }
    }

    private sealed class MoniepointWebhookData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }
}
