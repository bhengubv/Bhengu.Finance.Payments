// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayJustNow.Providers;

/// <summary>
/// PayJustNow Buy-Now-Pay-Later (BNPL) provider. 3 x interest-free instalments for South African
/// consumers. PayJustNow does NOT support payouts — <see cref="IPayoutProvider"/> is intentionally
/// not implemented.
/// </summary>
/// <remarks>
/// Subscriptions and mandates are exposed via sibling providers — the recurring-instalment
/// schedule maps to <see cref="ISubscriptionProvider"/>, and the underlying debit-order
/// authorisation maps to <see cref="IMandateProvider"/>.
/// </remarks>
public sealed class PayJustNowPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayJustNowOptions _options;
    private readonly PayJustNowIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayJustNow;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.Mandates |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PayJustNowPaymentProvider(
        HttpClient httpClient,
        IOptions<PayJustNowOptions> options,
        ILogger<PayJustNowPaymentProvider> logger,
        PayJustNowIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.MerchantId)} is required");

        PayJustNowHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency,
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct), ct),
            ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
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

        var body = await PayJustNowHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "orders", requestBody, "ProcessPayment", ct, request.IdempotencyKey).ConfigureAwait(false);
        var pjnResponse = JsonSerializer.Deserialize<PjnOrderResponse>(body, PayJustNowHttpClient.Json);

        Logger.LogInformation("PayJustNow order created: {OrderId} status={Status}",
            pjnResponse?.OrderId, pjnResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = pjnResponse?.OrderId ?? string.Empty,
            Status = MapStatus(pjnResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = pjnResponse?.CheckoutUrl is { Length: > 0 } url ? url : null,
            Message = "BNPL plan created"
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference,
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct), ct),
            ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var amountInCents = (int)(request.Amount * 100);
        var requestBody = new
        {
            order_id = request.GatewayReference,
            amount = amountInCents,
            reason = request.Reason
        };

        var body = await PayJustNowHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "refunds", requestBody, "ProcessRefund", ct, request.IdempotencyKey).ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<PjnRefundResponse>(body, PayJustNowHttpClient.Json);

        Logger.LogInformation("PayJustNow refund created: {RefundId} for order {OrderId}",
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

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            Logger.LogWarning("PayJustNow SecretKey not configured — signature verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        return RunWebhookVerify(() => SignatureHelpers.VerifyHmacSha256(payload, signature, _options.SecretKey));
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload)
    {
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<PjnWebhookEvent>(payload, PayJustNowHttpClient.Json);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed PayJustNow webhook event: {EventType} for order {OrderId}",
                webhookEvent.EventType, webhookEvent.OrderId);

            var reference = webhookEvent.OrderId;
            if (string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            var amount = webhookEvent.Amount.HasValue ? webhookEvent.Amount.Value / 100m : 0m;
            var currency = webhookEvent.Currency ?? "ZAR";

            return Task.FromResult<WebhookEvent?>(webhookEvent.EventType?.ToLowerInvariant() switch
            {
                "order.approved" or "order.completed" => new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = webhookEvent.CustomerToken
                },
                "order.declined" or "order.cancelled" => new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.EventType,
                    FailureMessage = webhookEvent.Reason
                },
                "instalment.paid" => new SubscriptionRenewedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.SubscriptionRenewed,
                    SubscriptionReference = reference,
                    Amount = amount,
                    Currency = currency,
                    NextBillingAt = webhookEvent.NextInstalmentAt
                },
                "instalment.overdue" or "instalment.failed" => new SubscriptionChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.SubscriptionChargeFailed,
                    SubscriptionReference = reference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.EventType,
                    NextRetryAt = webhookEvent.NextInstalmentAt
                },
                "refund.approved" => new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = webhookEvent.RefundId ?? reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = webhookEvent.IsPartial ?? false
                },
                "mandate.activated" or "agreement.activated" => new MandateActivatedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.MandateActivated,
                    MandateReference = reference,
                    AmountLimit = amount,
                    Currency = currency
                },
                "mandate.cancelled" or "agreement.cancelled" => new MandateCancelledEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Cancelled,
                    EventType = webhookEvent.EventType,
                    Category = WebhookEventCategory.MandateCancelled,
                    MandateReference = reference,
                    CancellationReason = webhookEvent.Reason
                },
                _ => null
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse PayJustNow webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
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
        [JsonPropertyName("amount")] public int? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("refund_id")] public string? RefundId { get; set; }
        [JsonPropertyName("customer_token")] public string? CustomerToken { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("is_partial")] public bool? IsPartial { get; set; }
        [JsonPropertyName("next_instalment_at")] public DateTime? NextInstalmentAt { get; set; }
    }
}
