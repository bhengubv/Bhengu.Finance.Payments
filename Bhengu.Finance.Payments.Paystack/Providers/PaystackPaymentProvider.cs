// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
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
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
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
    private readonly PaystackIdempotencyCache _idempotency;

    public string ProviderName => ProviderNames.Paystack;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.SyncSettlement |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.Disputes |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Marketplace |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>
    /// Construct the provider. The <paramref name="idempotency"/> cache is shared with the
    /// sibling Paystack providers so a single key dedupes across capabilities (e.g. retrying
    /// a charge that subsequently triggered a tokenisation will not double-execute either).
    /// </summary>
    public PaystackPaymentProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackPaymentProvider> logger,
        PaystackIdempotencyCache? idempotency = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency ?? new PaystackIdempotencyCache();

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.paystack.co/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PaystackObservability.ObserveChargeAsync(request.Currency, () =>
            _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct)));
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
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
            reference = request.IdempotencyKey ?? $"paystack-{Guid.NewGuid():N}"
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

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PaystackObservability.ObservePayoutAsync(request.Currency, () =>
            _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPayoutCoreAsync(request, ct)));
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
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
            reference = request.IdempotencyKey ?? $"transfer-{Guid.NewGuid():N}"
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

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PaystackObservability.ObserveRefundAsync(request.GatewayReference, () =>
            _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct)));
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
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

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.SecretKey : _options.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Paystack webhook secret not configured — signature verification cannot succeed.");
            PaystackObservability.RecordWebhookVerification(false);
            return false;
        }

        bool verified;
        try
        {
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            verified = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paystack webhook signature verification raised");
            verified = false;
        }
        PaystackObservability.RecordWebhookVerification(verified);
        return verified;
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return PaystackObservability.ObserveWebhookAsync(() => ParseWebhookCoreAsync(payload, ct));
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<PaystackWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Paystack webhook event: {EventType}", webhookEvent.Event);

            var typed = MapWebhookEvent(webhookEvent);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Paystack webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapWebhookEvent(PaystackWebhookEvent webhookEvent)
    {
        var data = webhookEvent.Data;
        var eventName = webhookEvent.Event?.ToLowerInvariant();
        var amount = data?.Amount / 100m ?? 0m;
        var currency = data?.Currency ?? "NGN";
        var rawReference = data?.Reference ?? string.Empty;

        switch (eventName)
        {
            case "charge.success":
                return new ChargeSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = data?.Customer?.CustomerCode,
                    PaymentMethodToken = data?.Authorization?.AuthorizationCode
                };

            case "charge.failed":
                return new ChargeFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.GatewayResponse,
                    FailureMessage = data?.Message
                };

            case "refund.processed":
                return new RefundSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = data?.Id?.ToString(CultureInfo.InvariantCulture) ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = data?.RefundedBy is not null && data?.Amount > 0 && data?.Amount < (data?.TransactionAmount ?? long.MaxValue)
                };

            case "refund.failed":
                return new RefundFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.Message
                };

            case "charge.dispute.create":
                return new DisputeOpenedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.DisputeOpened,
                    DisputeReference = data?.Id?.ToString(CultureInfo.InvariantCulture) ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    ReasonCode = data?.Category,
                    EvidenceDueBy = data?.DueAt
                };

            case "charge.dispute.resolve":
                {
                    var resolution = data?.Resolution?.ToLowerInvariant();
                    var won = resolution is "merchant-accepted" or "merchant-won" or "declined" || resolution is null;
                    return won
                        ? new DisputeWonEvent
                        {
                            GatewayReference = rawReference,
                            Status = PaymentStatus.Completed,
                            EventType = webhookEvent.Event,
                            Category = WebhookEventCategory.DisputeWon,
                            DisputeReference = data?.Id?.ToString(CultureInfo.InvariantCulture) ?? rawReference,
                            Amount = amount,
                            Currency = currency
                        }
                        : new DisputeLostEvent
                        {
                            GatewayReference = rawReference,
                            Status = PaymentStatus.Refunded,
                            EventType = webhookEvent.Event,
                            Category = WebhookEventCategory.DisputeLost,
                            DisputeReference = data?.Id?.ToString(CultureInfo.InvariantCulture) ?? rawReference,
                            Amount = amount,
                            Currency = currency,
                            ChargebackFee = null
                        };
                }

            case "subscription.create":
                return new SubscriptionCreatedEvent
                {
                    GatewayReference = data?.SubscriptionCode ?? rawReference,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.SubscriptionCreated,
                    SubscriptionReference = data?.SubscriptionCode ?? rawReference,
                    PlanReference = data?.Plan?.PlanCode ?? string.Empty,
                    CustomerId = data?.Customer?.CustomerCode,
                    NextBillingAt = data?.NextPaymentDate
                };

            case "invoice.payment_failed":
                return new SubscriptionChargeFailedEvent
                {
                    GatewayReference = data?.SubscriptionCode ?? rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.SubscriptionChargeFailed,
                    SubscriptionReference = data?.Subscription?.SubscriptionCode ?? data?.SubscriptionCode ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    NextRetryAt = data?.NextPaymentDate
                };

            case "subscription.disable":
            case "subscription.not_renew":
                return new SubscriptionCancelledEvent
                {
                    GatewayReference = data?.SubscriptionCode ?? rawReference,
                    Status = PaymentStatus.Cancelled,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.SubscriptionCancelled,
                    SubscriptionReference = data?.SubscriptionCode ?? rawReference,
                    CancellationReason = data?.Status
                };

            case "transfer.success":
                return new PayoutCompletedEvent
                {
                    GatewayReference = data?.TransferCode ?? rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = data?.TransferCode ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = data?.Recipient?.RecipientCode
                };

            case "transfer.failed":
            case "transfer.reversed":
                return new PayoutFailedEvent
                {
                    GatewayReference = data?.TransferCode ?? rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = data?.TransferCode ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.GatewayResponse
                };

            default:
                // Unknown — fall back to legacy untyped status mapping for backward compatibility.
                var status = eventName switch
                {
                    "charge.success" or "transfer.success" => PaymentStatus.Completed,
                    "charge.failed" or "transfer.failed" => PaymentStatus.Failed,
                    "refund.processing" or "refund.created" => PaymentStatus.Refunded,
                    _ => (PaymentStatus?)null
                };
                if (status is null || string.IsNullOrEmpty(rawReference)) return null;
                return new WebhookEvent
                {
                    GatewayReference = rawReference,
                    Status = status.Value,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.Unknown
                };
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
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("gateway_response")] public string? GatewayResponse { get; set; }
        [JsonPropertyName("transaction_amount")] public long? TransactionAmount { get; set; }
        [JsonPropertyName("refunded_by")] public string? RefundedBy { get; set; }
        [JsonPropertyName("resolution")] public string? Resolution { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("due_at")] public DateTime? DueAt { get; set; }
        [JsonPropertyName("subscription_code")] public string? SubscriptionCode { get; set; }
        [JsonPropertyName("next_payment_date")] public DateTime? NextPaymentDate { get; set; }
        [JsonPropertyName("transfer_code")] public string? TransferCode { get; set; }
        [JsonPropertyName("customer")] public PaystackWebhookCustomer? Customer { get; set; }
        [JsonPropertyName("authorization")] public PaystackWebhookAuthorization? Authorization { get; set; }
        [JsonPropertyName("plan")] public PaystackWebhookPlan? Plan { get; set; }
        [JsonPropertyName("subscription")] public PaystackWebhookSubscription? Subscription { get; set; }
        [JsonPropertyName("recipient")] public PaystackWebhookRecipient? Recipient { get; set; }
    }

    private sealed class PaystackWebhookCustomer
    {
        [JsonPropertyName("customer_code")] public string? CustomerCode { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class PaystackWebhookAuthorization
    {
        [JsonPropertyName("authorization_code")] public string? AuthorizationCode { get; set; }
    }

    private sealed class PaystackWebhookPlan
    {
        [JsonPropertyName("plan_code")] public string? PlanCode { get; set; }
    }

    private sealed class PaystackWebhookSubscription
    {
        [JsonPropertyName("subscription_code")] public string? SubscriptionCode { get; set; }
    }

    private sealed class PaystackWebhookRecipient
    {
        [JsonPropertyName("recipient_code")] public string? RecipientCode { get; set; }
    }
}
