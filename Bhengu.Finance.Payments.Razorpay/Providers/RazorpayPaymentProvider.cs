// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay (India) payment gateway provider. Wraps the Razorpay REST API and supports
/// the server-side capture of a pre-authorised payment_id from a client-side checkout,
/// refunds, and RazorpayX payouts.
/// </summary>
/// <remarks>
/// PaymentRequest.PaymentMethodToken is expected to be a Razorpay <c>payment_id</c>
/// returned by the Razorpay client-side checkout. The provider issues
/// <c>POST /v1/payments/{paymentId}/capture</c> to settle it.
/// To use the Orders flow instead, pass <c>"order"</c> as the <c>flow</c> key in Metadata
/// and the SDK will create an order and surface the order_id + checkout URL in the response.
/// </remarks>
public sealed class RazorpayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly RazorpayOptions _options;

    public override string ProviderName => ProviderNames.Razorpay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Mandates |
        ProviderCapabilities.Marketplace |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Disputes |
        ProviderCapabilities.ThreeDSecure;

    public RazorpayPaymentProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayPaymentProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.KeyId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RazorpayOptions.KeyId)} is required");
        if (string.IsNullOrWhiteSpace(_options.KeySecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RazorpayOptions.KeySecret)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.razorpay.com/");

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.KeyId}:{_options.KeySecret}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var amountInPaise = (long)(request.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? _options.Currency
            : request.Currency.ToUpperInvariant();

        var flow = request.Metadata?.GetValueOrDefault("flow")?.ToLowerInvariant();

        if (flow == "order")
        {
            // Orders flow — create a Razorpay order. Callers redirect the customer to the
            // hosted checkout with the returned order_id; settlement is later confirmed via webhook.
            var orderBody = new
            {
                amount = amountInPaise,
                currency,
                receipt = request.Metadata?.GetValueOrDefault("receipt") ?? $"rcpt_{Guid.NewGuid():N}",
                notes = request.Metadata ?? new Dictionary<string, string>().AsReadOnly() as IReadOnlyDictionary<string, string>,
                partial_payment = false
            };

            var orderRaw = await SendAsync(HttpMethod.Post, "v1/orders", orderBody, ct, "CreateOrder", request.IdempotencyKey).ConfigureAwait(false);
            var order = JsonSerializer.Deserialize<RazorpayOrderResponse>(orderRaw);

            Logger.LogInformation("Razorpay order created: {OrderId} status={Status}", order?.Id, order?.Status);

            return new PaymentResponse
            {
                GatewayReference = order?.Id ?? string.Empty,
                Status = MapStatus(order?.Status ?? "created"),
                Amount = request.Amount,
                Currency = currency,
                ProcessedAt = DateTime.UtcNow,
                Message = $"Razorpay order created — direct customer to checkout with order_id={order?.Id}"
            };
        }

        // Default flow — capture a pre-authorised payment_id.
        var captureBody = new
        {
            amount = amountInPaise,
            currency
        };

        var raw = await SendAsync(
            HttpMethod.Post,
            $"v1/payments/{Uri.EscapeDataString(request.PaymentMethodToken)}/capture",
            captureBody, ct, "CapturePayment", request.IdempotencyKey).ConfigureAwait(false);
        var payment = JsonSerializer.Deserialize<RazorpayPaymentResponse>(raw);

        Logger.LogInformation("Razorpay payment captured: {PaymentId} status={Status}", payment?.Id, payment?.Status);

        return new PaymentResponse
        {
            GatewayReference = payment?.Id ?? request.PaymentMethodToken,
            Status = MapStatus(payment?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            Message = payment?.Status
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var amountInPaise = (long)(request.Amount * 100);
        var refundBody = new
        {
            amount = amountInPaise,
            speed = "normal",
            notes = new Dictionary<string, string> { ["reason"] = request.Reason ?? "Customer refund" }
        };

        var raw = await SendAsync(
            HttpMethod.Post,
            $"v1/payments/{Uri.EscapeDataString(request.GatewayReference)}/refund",
            refundBody, ct, "ProcessRefund", request.IdempotencyKey).ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<RazorpayRefundResponse>(raw);

        Logger.LogInformation("Razorpay refund created: {RefundId} for payment {PaymentId}",
            refund?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refund?.Id ?? string.Empty,
            Amount = request.Amount,
            Status = MapStatus(refund?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Status
        };
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.RazorpayXAccountNumber))
            throw new ProviderConfigurationException(ProviderName,
                $"{nameof(RazorpayOptions.RazorpayXAccountNumber)} is required for RazorpayX payouts");

        var amountInPaise = (long)(request.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? _options.Currency
            : request.Currency.ToUpperInvariant();

        var payoutBody = new
        {
            account_number = _options.RazorpayXAccountNumber,
            amount = amountInPaise,
            currency,
            mode = "IMPS",
            purpose = "payout",
            fund_account_id = request.DestinationToken,
            queue_if_low_balance = true,
            reference_id = $"payout-{Guid.NewGuid():N}",
            narration = request.Description ?? "Bhengu payout"
        };

        var raw = await SendAsync(HttpMethod.Post, "v1/payouts", payoutBody, ct, "ProcessPayout", request.IdempotencyKey).ConfigureAwait(false);
        var payout = JsonSerializer.Deserialize<RazorpayPayoutResponse>(raw);

        Logger.LogInformation("Razorpay payout created: {PayoutId} status={Status}", payout?.Id, payout?.Status);

        return new PayoutResponse
        {
            GatewayReference = payout?.Id ?? string.Empty,
            Status = MapStatus(payout?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            Logger.LogWarning("Razorpay WebhookSecret not configured — signature verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        return RunWebhookVerify(() => SignatureHelpers.VerifyHmacSha256(payload, signature, _options.WebhookSecret));
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload, ct), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<RazorpayWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Razorpay webhook event: {EventType}", webhookEvent.Event);

            var typed = MapTypedEvent(webhookEvent);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Razorpay webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // Map Razorpay event name → strongly-typed Bhengu webhook sub-record.
    // Unrecognised events return null so callers don't get false-positive signals.
    private static WebhookEvent? MapTypedEvent(RazorpayWebhookEvent evt)
    {
        var eventName = evt.Event?.ToLowerInvariant();
        var payment = evt.Payload?.Payment?.Entity;
        var refund = evt.Payload?.Refund?.Entity;
        var payout = evt.Payload?.Payout?.Entity;
        var subscription = evt.Payload?.Subscription?.Entity;
        var settlement = evt.Payload?.Settlement?.Entity;
        var token = evt.Payload?.Token?.Entity;
        var dispute = evt.Payload?.Dispute?.Entity;

        switch (eventName)
        {
            case "payment.captured":
                if (payment is null) return null;
                return new ChargeSucceededEvent
                {
                    GatewayReference = payment.Id ?? string.Empty,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = payment.Amount / 100m,
                    Currency = payment.Currency ?? "INR",
                    CustomerId = payment.CustomerId,
                    PaymentMethodToken = payment.TokenId ?? payment.Id
                };

            case "payment.failed":
                if (payment is null) return null;
                return new ChargeFailedEvent
                {
                    GatewayReference = payment.Id ?? string.Empty,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = payment.Amount / 100m,
                    Currency = payment.Currency ?? "INR",
                    FailureCode = payment.ErrorCode,
                    FailureMessage = payment.ErrorDescription
                };

            case "payment.authorized":
                if (payment is null) return null;
                return new ChargePendingEvent
                {
                    GatewayReference = payment.Id ?? string.Empty,
                    Status = PaymentStatus.Pending,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = payment.Amount / 100m,
                    Currency = payment.Currency ?? "INR"
                };

            case "refund.processed":
                if (refund is null) return null;
                return new RefundSucceededEvent
                {
                    GatewayReference = refund.PaymentId ?? string.Empty,
                    Status = PaymentStatus.Refunded,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = refund.Id ?? string.Empty,
                    Amount = refund.Amount / 100m,
                    Currency = refund.Currency ?? "INR",
                    IsPartial = false
                };

            case "refund.failed":
                if (refund is null) return null;
                return new RefundFailedEvent
                {
                    GatewayReference = refund.PaymentId ?? string.Empty,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = refund.Amount / 100m,
                    Currency = refund.Currency ?? "INR"
                };

            case "subscription.activated":
                if (subscription is null) return null;
                return new SubscriptionCreatedEvent
                {
                    GatewayReference = subscription.Id ?? string.Empty,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.SubscriptionCreated,
                    SubscriptionReference = subscription.Id ?? string.Empty,
                    PlanReference = subscription.PlanId ?? string.Empty,
                    CustomerId = subscription.CustomerId,
                    NextBillingAt = subscription.ChargeAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(subscription.ChargeAt.Value).UtcDateTime : null
                };

            case "subscription.charged":
                if (subscription is null) return null;
                return new SubscriptionRenewedEvent
                {
                    GatewayReference = subscription.Id ?? string.Empty,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.SubscriptionRenewed,
                    SubscriptionReference = subscription.Id ?? string.Empty,
                    Amount = payment?.Amount / 100m ?? 0m,
                    Currency = payment?.Currency ?? "INR",
                    NextBillingAt = subscription.ChargeAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(subscription.ChargeAt.Value).UtcDateTime : null
                };

            case "subscription.completed":
            case "subscription.cancelled":
            case "subscription.halted":
                if (subscription is null) return null;
                return new SubscriptionCancelledEvent
                {
                    GatewayReference = subscription.Id ?? string.Empty,
                    Status = PaymentStatus.Cancelled,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.SubscriptionCancelled,
                    SubscriptionReference = subscription.Id ?? string.Empty,
                    CancellationReason = eventName
                };

            case "subscription.pending":
                if (subscription is null) return null;
                return new SubscriptionChargeFailedEvent
                {
                    GatewayReference = subscription.Id ?? string.Empty,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.SubscriptionChargeFailed,
                    SubscriptionReference = subscription.Id ?? string.Empty,
                    Amount = 0m,
                    Currency = "INR"
                };

            case "payout.processed":
                if (payout is null) return null;
                return new PayoutCompletedEvent
                {
                    GatewayReference = payout.Id ?? string.Empty,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = payout.Id ?? string.Empty,
                    Amount = payout.Amount / 100m,
                    Currency = payout.Currency ?? "INR"
                };

            case "payout.failed":
            case "payout.reversed":
                if (payout is null) return null;
                return new PayoutFailedEvent
                {
                    GatewayReference = payout.Id ?? string.Empty,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = payout.Id ?? string.Empty,
                    Amount = payout.Amount / 100m,
                    Currency = payout.Currency ?? "INR",
                    FailureCode = eventName == "payout.reversed" ? "reversed" : payout.FailureReason
                };

            case "settlement.processed":
                if (settlement is null) return null;
                return new SettlementCompletedEvent
                {
                    GatewayReference = settlement.Id ?? string.Empty,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = settlement.Id ?? string.Empty,
                    NetAmount = settlement.Amount / 100m,
                    Currency = "INR",
                    Fees = (settlement.Fees + settlement.Tax) / 100m
                };

            case "token.confirmed":
                if (token is null) return null;
                return new MandateActivatedEvent
                {
                    GatewayReference = token.Id ?? string.Empty,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.MandateActivated,
                    MandateReference = token.Id ?? string.Empty,
                    AmountLimit = token.MaxAmount / 100m,
                    Currency = "INR"
                };

            case "token.cancelled":
                if (token is null) return null;
                return new MandateCancelledEvent
                {
                    GatewayReference = token.Id ?? string.Empty,
                    Status = PaymentStatus.Cancelled,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.MandateCancelled,
                    MandateReference = token.Id ?? string.Empty,
                    CancellationReason = "cancelled"
                };

            case "dispute.created":
                if (dispute is null) return null;
                return new DisputeOpenedEvent
                {
                    GatewayReference = dispute.PaymentId ?? string.Empty,
                    Status = PaymentStatus.Pending,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.DisputeOpened,
                    DisputeReference = dispute.Id ?? string.Empty,
                    Amount = dispute.Amount / 100m,
                    Currency = dispute.Currency ?? "INR",
                    ReasonCode = dispute.ReasonCode,
                    EvidenceDueBy = dispute.RespondBy is > 0 ? DateTimeOffset.FromUnixTimeSeconds(dispute.RespondBy.Value).UtcDateTime : null
                };

            case "dispute.won":
                if (dispute is null) return null;
                return new DisputeWonEvent
                {
                    GatewayReference = dispute.PaymentId ?? string.Empty,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.DisputeWon,
                    DisputeReference = dispute.Id ?? string.Empty,
                    Amount = dispute.Amount / 100m,
                    Currency = dispute.Currency ?? "INR"
                };

            case "dispute.lost":
                if (dispute is null) return null;
                return new DisputeLostEvent
                {
                    GatewayReference = dispute.PaymentId ?? string.Empty,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Event,
                    Category = WebhookEventCategory.DisputeLost,
                    DisputeReference = dispute.Id ?? string.Empty,
                    Amount = dispute.Amount / 100m,
                    Currency = dispute.Currency ?? "INR"
                };

            // legacy aliases preserved so existing consumers don't regress
            case "order.paid":
            case "refund.created":
                var fallbackRef = payment?.Id ?? refund?.PaymentId ?? evt.Payload?.Order?.Entity?.Id;
                if (string.IsNullOrEmpty(fallbackRef)) return null;
                return new WebhookEvent
                {
                    GatewayReference = fallbackRef,
                    Status = eventName == "order.paid" ? PaymentStatus.Completed : PaymentStatus.Refunded,
                    EventType = evt.Event,
                    Category = eventName == "order.paid" ? WebhookEventCategory.ChargeSucceeded : WebhookEventCategory.RefundSucceeded
                };

            default:
                return null;
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation, string? idempotencyKey = null)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // Razorpay accepts a custom header for POST idempotency. Honour the caller's key when supplied.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("X-Razorpay-IdempotencyKey", idempotencyKey);

        // HttpRequestException is auto-translated to ProviderUnavailableException by BhenguProviderBase.
        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Razorpay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "captured" or "paid" or "processed" or "success" or "succeeded" => PaymentStatus.Completed,
        "created" or "attempted" or "authorized" or "pending" or "queued" or "scheduled" or "initiated" => PaymentStatus.Pending,
        "failed" or "rejected" or "reversed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayOrderResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("receipt")] public string? Receipt { get; set; }
    }

    private sealed class RazorpayPaymentResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("method")] public string? Method { get; set; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
        [JsonPropertyName("token_id")] public string? TokenId { get; set; }
        [JsonPropertyName("error_code")] public string? ErrorCode { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }

    private sealed class RazorpayRefundResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("payment_id")] public string? PaymentId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class RazorpayPayoutResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("mode")] public string? Mode { get; set; }
        [JsonPropertyName("failure_reason")] public string? FailureReason { get; set; }
    }

    private sealed class RazorpayWebhookSubscriptionEntity
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("plan_id")] public string? PlanId { get; set; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("charge_at")] public long? ChargeAt { get; set; }
    }

    private sealed class RazorpayWebhookSubscriptionWrapper
    {
        [JsonPropertyName("entity")] public RazorpayWebhookSubscriptionEntity? Entity { get; set; }
    }

    private sealed class RazorpayWebhookSettlementEntity
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("fees")] public long Fees { get; set; }
        [JsonPropertyName("tax")] public long Tax { get; set; }
    }

    private sealed class RazorpayWebhookSettlementWrapper
    {
        [JsonPropertyName("entity")] public RazorpayWebhookSettlementEntity? Entity { get; set; }
    }

    private sealed class RazorpayWebhookTokenEntity
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("max_amount")] public long MaxAmount { get; set; }
        [JsonPropertyName("recurring_status")] public string? Status { get; set; }
    }

    private sealed class RazorpayWebhookTokenWrapper
    {
        [JsonPropertyName("entity")] public RazorpayWebhookTokenEntity? Entity { get; set; }
    }

    private sealed class RazorpayWebhookDisputeEntity
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("payment_id")] public string? PaymentId { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
        [JsonPropertyName("respond_by")] public long? RespondBy { get; set; }
    }

    private sealed class RazorpayWebhookDisputeWrapper
    {
        [JsonPropertyName("entity")] public RazorpayWebhookDisputeEntity? Entity { get; set; }
    }

    private sealed class RazorpayWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("payload")] public RazorpayWebhookPayload? Payload { get; set; }
    }

    private sealed class RazorpayWebhookPayload
    {
        [JsonPropertyName("payment")] public RazorpayWebhookPaymentWrapper? Payment { get; set; }
        [JsonPropertyName("refund")] public RazorpayWebhookRefundWrapper? Refund { get; set; }
        [JsonPropertyName("order")] public RazorpayWebhookOrderWrapper? Order { get; set; }
        [JsonPropertyName("payout")] public RazorpayWebhookPayoutWrapper? Payout { get; set; }
        [JsonPropertyName("subscription")] public RazorpayWebhookSubscriptionWrapper? Subscription { get; set; }
        [JsonPropertyName("settlement")] public RazorpayWebhookSettlementWrapper? Settlement { get; set; }
        [JsonPropertyName("token")] public RazorpayWebhookTokenWrapper? Token { get; set; }
        [JsonPropertyName("dispute")] public RazorpayWebhookDisputeWrapper? Dispute { get; set; }
    }

    private sealed class RazorpayWebhookPaymentWrapper
    {
        [JsonPropertyName("entity")] public RazorpayPaymentResponse? Entity { get; set; }
    }

    private sealed class RazorpayWebhookRefundWrapper
    {
        [JsonPropertyName("entity")] public RazorpayRefundResponse? Entity { get; set; }
    }

    private sealed class RazorpayWebhookOrderWrapper
    {
        [JsonPropertyName("entity")] public RazorpayOrderResponse? Entity { get; set; }
    }

    private sealed class RazorpayWebhookPayoutWrapper
    {
        [JsonPropertyName("entity")] public RazorpayPayoutResponse? Entity { get; set; }
    }
}
