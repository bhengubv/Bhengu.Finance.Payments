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
using Bhengu.Finance.Payments.Paystack.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paystack.Providers;

/// <summary>
/// Paystack (Nigeria / Africa) payment gateway provider. Wraps the Paystack REST API
/// and supports payments (charge_authorization), transfers (payouts) and refunds.
/// </summary>
public sealed class PaystackPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;
    private readonly ILogger<PaystackPaymentProvider> _logger;

    public string ProviderName => ProviderNames.Paystack;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.SyncSettlement |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer;

    public PaystackPaymentProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.paystack.co/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInSmallestUnit = (long)(request.Amount * 100);
        var email = request.Metadata?.GetValueOrDefault("email") ?? _options.DefaultEmail;
        if (string.IsNullOrWhiteSpace(email))
            throw new PaymentDeclinedException(ProviderName, "missing_email",
                "Paystack requires an 'email' in PaymentRequest.Metadata or PaystackOptions.DefaultEmail.");

        var requestBody = new
        {
            authorization_code = request.PaymentMethodToken,
            email,
            amount = amountInSmallestUnit,
            currency = request.Currency.ToUpperInvariant(),
            metadata = request.Metadata ?? new Dictionary<string, string>().AsReadOnly() as IReadOnlyDictionary<string, string>,
            reference = $"paystack-{Guid.NewGuid():N}"
        };

        var body = await SendAsync(HttpMethod.Post, "transaction/charge_authorization", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var paystackResponse = JsonSerializer.Deserialize<PaystackTransactionResponse>(body);

        _logger.LogInformation("Paystack charge created: {Reference} status={Status}",
            paystackResponse?.Data?.Reference, paystackResponse?.Data?.Status);

        return new PaymentResponse
        {
            GatewayReference = paystackResponse?.Data?.Reference ?? string.Empty,
            Status = MapStatus(paystackResponse?.Data?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = paystackResponse?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var recipientCode = request.DestinationToken.StartsWith("recipient-", StringComparison.Ordinal)
            ? request.DestinationToken["recipient-".Length..]
            : request.DestinationToken;

        var amountInSmallestUnit = (long)(request.Amount * 100);
        var requestBody = new
        {
            source = "balance",
            recipient = recipientCode,
            amount = amountInSmallestUnit,
            currency = request.Currency.ToUpperInvariant(),
            reason = request.Description,
            reference = $"transfer-{Guid.NewGuid():N}"
        };

        var body = await SendAsync(HttpMethod.Post, "transfer", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var paystackResponse = JsonSerializer.Deserialize<PaystackTransferResponse>(body);

        _logger.LogInformation("Paystack transfer created: {Reference} status={Status}",
            paystackResponse?.Data?.Reference, paystackResponse?.Data?.Status);

        return new PayoutResponse
        {
            GatewayReference = paystackResponse?.Data?.Reference ?? string.Empty,
            Status = MapStatus(paystackResponse?.Data?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInSmallestUnit = (long)(request.Amount * 100);
        var requestBody = new
        {
            transaction = request.GatewayReference,
            amount = amountInSmallestUnit
        };

        var body = await SendAsync(HttpMethod.Post, "refund", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<PaystackRefundResponse>(body);

        _logger.LogInformation("Paystack refund created: {RefundId} for transaction {TransactionId}",
            refundResponse?.Data?.RefundReference, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Data?.RefundReference ?? string.Empty,
            Amount = request.Amount,
            Status = MapStatus(refundResponse?.Data?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Message
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.SecretKey : _options.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Paystack webhook secret not configured — signature verification cannot succeed.");
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
            _logger.LogError(ex, "Paystack webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<PaystackWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Paystack webhook event: {EventType}", webhookEvent.Event);

            var status = webhookEvent.Event?.ToLowerInvariant() switch
            {
                "charge.success" or "transfer.success" => PaymentStatus.Completed,
                "charge.failed" or "transfer.failed" => PaymentStatus.Failed,
                "refund.processed" or "refund.processing" or "refund.created" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(webhookEvent.Data?.Reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhookEvent.Data.Reference,
                Status = status.Value,
                EventType = webhookEvent.Event
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Paystack webhook event");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Paystack failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Paystack {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "succeeded" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" or "ongoing" => PaymentStatus.Pending,
        "failed" or "abandoned" or "reversed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" or "refund_processed" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Paystack API response shapes (internal) ===

    private sealed class PaystackTransactionResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackTransactionData? Data { get; set; }
    }

    private sealed class PaystackTransactionData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class PaystackTransferResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackTransferData? Data { get; set; }
    }

    private sealed class PaystackTransferData
    {
        [JsonPropertyName("transfer_code")] public string? TransferCode { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
    }

    private sealed class PaystackRefundResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackRefundData? Data { get; set; }
    }

    private sealed class PaystackRefundData
    {
        [JsonPropertyName("transaction_id")] public long TransactionId { get; set; }
        [JsonPropertyName("refund_reference")] public string? RefundReference { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class PaystackWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public PaystackWebhookData? Data { get; set; }
    }

    private sealed class PaystackWebhookData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
    }
}
