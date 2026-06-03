// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe payment gateway provider. Wraps the official <c>Stripe.net</c> SDK and conforms
/// to the Bhengu.Finance.Payments contract. Supports payments, payouts (via Stripe Connect /
/// platform balance), refunds and webhook verification.
/// </summary>
public sealed class StripePaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly StripeOptions _options;
    private readonly ILogger<StripePaymentProvider> _logger;
    private readonly IStripeClient _stripeClient;

    public string ProviderName => ProviderNames.Stripe;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.SyncSettlement |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.ThreeDSecure |
        ProviderCapabilities.Disputes |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Mandates |
        ProviderCapabilities.Marketplace |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks |
        ProviderCapabilities.PartialRefund;

    public StripePaymentProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripePaymentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StripeOptions.SecretKey)} is required");

        // Build a StripeClient that routes through the injected HttpClient. This is what makes the
        // provider unit-testable end-to-end (HttpMessageHandler stubs intercept the requests) and
        // also gives the consumer control over connection pooling, telemetry handlers, etc.
        // StripeConfiguration.ApiKey is still set as a fallback for any Stripe.net code paths that
        // bypass the client (e.g. EventUtility, which uses the static config).
        StripeConfiguration.ApiKey = _options.SecretKey;
        _stripeClient = new StripeClient(
            apiKey: _options.SecretKey,
            httpClient: new SystemNetHttpClient(httpClient));
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (long)(request.Amount * 100);
        var metadata = request.Metadata?.ToDictionary(k => k.Key, v => v.Value) ?? new Dictionary<string, string>();

        var options = new PaymentIntentCreateOptions
        {
            Amount = amountInCents,
            Currency = request.Currency.ToLowerInvariant(),
            PaymentMethod = request.PaymentMethodToken,
            Description = request.Description,
            ConfirmationMethod = "automatic",
            Confirm = true,
            Metadata = metadata
        };

        try
        {
            var service = new PaymentIntentService(_stripeClient);
            var paymentIntent = await service.CreateAsync(options, BuildRequestOptions(request.IdempotencyKey), ct).ConfigureAwait(false);

            _logger.LogInformation("Stripe PaymentIntent created: {Id} status={Status}",
                paymentIntent.Id, paymentIntent.Status);

            return new PaymentResponse
            {
                GatewayReference = paymentIntent.Id,
                Status = MapStatus(paymentIntent.Status),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = paymentIntent.Status
            };
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ProcessPayment");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (long)(request.Amount * 100);
        var options = new PayoutCreateOptions
        {
            Amount = amountInCents,
            Currency = request.Currency.ToLowerInvariant(),
            Description = request.Description,
            Destination = request.DestinationToken
        };

        try
        {
            var service = new PayoutService(_stripeClient);
            var payout = await service.CreateAsync(options, BuildRequestOptions(request.IdempotencyKey), ct).ConfigureAwait(false);

            _logger.LogInformation("Stripe Payout created: {Id} status={Status}", payout.Id, payout.Status);

            return new PayoutResponse
            {
                GatewayReference = payout.Id,
                Status = MapStatus(payout.Status),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ProcessPayout");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (long)(request.Amount * 100);
        var options = new RefundCreateOptions
        {
            PaymentIntent = request.GatewayReference,
            Amount = amountInCents,
            Reason = MapRefundReason(request.Reason)
        };

        try
        {
            var service = new RefundService(_stripeClient);
            var refund = await service.CreateAsync(options, BuildRequestOptions(request.IdempotencyKey), ct).ConfigureAwait(false);

            _logger.LogInformation("Stripe Refund created: {Id} for PaymentIntent {PaymentIntent}",
                refund.Id, request.GatewayReference);

            return new RefundResponse
            {
                GatewayReference = refund.Id,
                Amount = request.Amount,
                Status = MapStatus(refund.Status),
                ProcessedAt = DateTime.UtcNow,
                Message = refund.Status
            };
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ProcessRefund");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Stripe WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            EventUtility.ConstructEvent(payload, signature, _options.WebhookSecret);
            return true;
        }
        catch (StripeException ex)
        {
            _logger.LogWarning("Stripe webhook signature verification failed: {Error}", ex.Message);
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            // throwOnApiVersionMismatch:false lets the SDK keep up across Stripe API minor versions
            // without forcing every consumer to upgrade Stripe.net the day the new version ships.
            var stripeEvent = EventUtility.ParseEvent(payload, throwOnApiVersionMismatch: false);
            _logger.LogInformation("Parsed Stripe webhook event: {EventType}", stripeEvent.Type);

            var typed = MapStripeEvent(stripeEvent);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Stripe webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapStripeEvent(Event stripeEvent)
    {
        switch (stripeEvent.Type)
        {
            case "charge.succeeded":
            {
                if (stripeEvent.Data.Object is not Charge ch) return null;
                return new ChargeSucceededEvent
                {
                    GatewayReference = ch.PaymentIntentId ?? ch.Id,
                    Status = PaymentStatus.Completed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = ch.Amount / 100m,
                    Currency = (ch.Currency ?? "usd").ToUpperInvariant(),
                    CustomerId = ch.CustomerId,
                    PaymentMethodToken = ch.PaymentMethod
                };
            }
            case "charge.failed":
            {
                if (stripeEvent.Data.Object is not Charge ch) return null;
                return new ChargeFailedEvent
                {
                    GatewayReference = ch.PaymentIntentId ?? ch.Id,
                    Status = PaymentStatus.Failed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = ch.Amount / 100m,
                    Currency = (ch.Currency ?? "usd").ToUpperInvariant(),
                    FailureCode = ch.FailureCode,
                    FailureMessage = ch.FailureMessage
                };
            }
            case "charge.pending":
            {
                if (stripeEvent.Data.Object is not Charge ch) return null;
                return new ChargePendingEvent
                {
                    GatewayReference = ch.PaymentIntentId ?? ch.Id,
                    Status = PaymentStatus.Pending,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = ch.Amount / 100m,
                    Currency = (ch.Currency ?? "usd").ToUpperInvariant()
                };
            }
            case "payment_intent.succeeded":
            {
                if (stripeEvent.Data.Object is not PaymentIntent pi) return null;
                return new ChargeSucceededEvent
                {
                    GatewayReference = pi.Id,
                    Status = PaymentStatus.Completed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = pi.Amount / 100m,
                    Currency = (pi.Currency ?? "usd").ToUpperInvariant(),
                    CustomerId = pi.CustomerId,
                    PaymentMethodToken = pi.PaymentMethodId
                };
            }
            case "payment_intent.payment_failed":
            {
                if (stripeEvent.Data.Object is not PaymentIntent pi) return null;
                return new ChargeFailedEvent
                {
                    GatewayReference = pi.Id,
                    Status = PaymentStatus.Failed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = pi.Amount / 100m,
                    Currency = (pi.Currency ?? "usd").ToUpperInvariant(),
                    FailureCode = pi.LastPaymentError?.Code,
                    FailureMessage = pi.LastPaymentError?.Message
                };
            }
            case "payment_intent.canceled":
            {
                if (stripeEvent.Data.Object is not PaymentIntent pi) return null;
                return new WebhookEvent
                {
                    GatewayReference = pi.Id,
                    Status = PaymentStatus.Cancelled,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.Unknown
                };
            }
            case "charge.refunded":
            {
                if (stripeEvent.Data.Object is not Charge ch) return null;
                var refund = ch.Refunds?.Data?.LastOrDefault();
                var isPartial = ch.AmountRefunded < ch.Amount;
                return new RefundSucceededEvent
                {
                    GatewayReference = ch.PaymentIntentId ?? ch.Id,
                    Status = PaymentStatus.Refunded,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = refund?.Id ?? ch.Id,
                    Amount = (refund?.Amount ?? ch.AmountRefunded) / 100m,
                    Currency = (ch.Currency ?? "usd").ToUpperInvariant(),
                    IsPartial = isPartial
                };
            }
            case "refund.created":
            case "refund.updated":
            {
                if (stripeEvent.Data.Object is not Refund r) return null;
                if (r.Status?.Equals("succeeded", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new RefundSucceededEvent
                    {
                        GatewayReference = r.PaymentIntentId ?? r.ChargeId ?? r.Id,
                        Status = PaymentStatus.Refunded,
                        EventType = stripeEvent.Type,
                        Category = WebhookEventCategory.RefundSucceeded,
                        RefundReference = r.Id,
                        Amount = r.Amount / 100m,
                        Currency = (r.Currency ?? "usd").ToUpperInvariant(),
                        IsPartial = false
                    };
                }
                return null;
            }
            case "refund.failed":
            {
                if (stripeEvent.Data.Object is not Refund r) return null;
                return new RefundFailedEvent
                {
                    GatewayReference = r.PaymentIntentId ?? r.ChargeId ?? r.Id,
                    Status = PaymentStatus.Failed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = r.Amount / 100m,
                    Currency = (r.Currency ?? "usd").ToUpperInvariant(),
                    FailureCode = r.FailureReason,
                    FailureMessage = r.FailureReason
                };
            }
            case "charge.dispute.created":
            {
                if (stripeEvent.Data.Object is not Dispute d) return null;
                return new DisputeOpenedEvent
                {
                    GatewayReference = d.ChargeId ?? d.PaymentIntentId ?? d.Id,
                    Status = PaymentStatus.Pending,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.DisputeOpened,
                    DisputeReference = d.Id,
                    Amount = d.Amount / 100m,
                    Currency = (d.Currency ?? "usd").ToUpperInvariant(),
                    ReasonCode = d.Reason,
                    EvidenceDueBy = d.EvidenceDetails?.DueBy
                };
            }
            case "charge.dispute.closed":
            {
                if (stripeEvent.Data.Object is not Dispute d) return null;
                if (d.Status?.Equals("won", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new DisputeWonEvent
                    {
                        GatewayReference = d.ChargeId ?? d.PaymentIntentId ?? d.Id,
                        Status = PaymentStatus.Completed,
                        EventType = stripeEvent.Type,
                        Category = WebhookEventCategory.DisputeWon,
                        DisputeReference = d.Id,
                        Amount = d.Amount / 100m,
                        Currency = (d.Currency ?? "usd").ToUpperInvariant()
                    };
                }
                if (d.Status?.Equals("lost", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new DisputeLostEvent
                    {
                        GatewayReference = d.ChargeId ?? d.PaymentIntentId ?? d.Id,
                        Status = PaymentStatus.Refunded,
                        EventType = stripeEvent.Type,
                        Category = WebhookEventCategory.DisputeLost,
                        DisputeReference = d.Id,
                        Amount = d.Amount / 100m,
                        Currency = (d.Currency ?? "usd").ToUpperInvariant(),
                        ChargebackFee = d.BalanceTransactions?.FirstOrDefault()?.Fee / 100m
                    };
                }
                return null;
            }
            case "customer.subscription.created":
            {
                if (stripeEvent.Data.Object is not Subscription sub) return null;
                return new SubscriptionCreatedEvent
                {
                    GatewayReference = sub.Id,
                    Status = PaymentStatus.Pending,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.SubscriptionCreated,
                    SubscriptionReference = sub.Id,
                    PlanReference = sub.Items?.Data?.FirstOrDefault()?.Plan?.Id ?? sub.Items?.Data?.FirstOrDefault()?.Price?.Id ?? string.Empty,
                    CustomerId = sub.CustomerId,
                    NextBillingAt = sub.CurrentPeriodEnd == default ? null : sub.CurrentPeriodEnd
                };
            }
            case "customer.subscription.updated":
            {
                if (stripeEvent.Data.Object is not Subscription sub) return null;
                if (sub.Status?.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new SubscriptionRenewedEvent
                    {
                        GatewayReference = sub.Id,
                        Status = PaymentStatus.Completed,
                        EventType = stripeEvent.Type,
                        Category = WebhookEventCategory.SubscriptionRenewed,
                        SubscriptionReference = sub.Id,
                        Amount = (sub.Items?.Data?.FirstOrDefault()?.Plan?.Amount ?? 0L) / 100m,
                        Currency = (sub.Currency ?? "usd").ToUpperInvariant(),
                        NextBillingAt = sub.CurrentPeriodEnd == default ? null : sub.CurrentPeriodEnd
                    };
                }
                return null;
            }
            case "customer.subscription.deleted":
            {
                if (stripeEvent.Data.Object is not Subscription sub) return null;
                return new SubscriptionCancelledEvent
                {
                    GatewayReference = sub.Id,
                    Status = PaymentStatus.Cancelled,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.SubscriptionCancelled,
                    SubscriptionReference = sub.Id,
                    CancellationReason = sub.CancellationDetails?.Reason
                };
            }
            case "invoice.payment_failed":
            {
                if (stripeEvent.Data.Object is not Invoice inv) return null;
                var subRef = inv.SubscriptionId ?? inv.Id;
                return new SubscriptionChargeFailedEvent
                {
                    GatewayReference = subRef,
                    Status = PaymentStatus.Failed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.SubscriptionChargeFailed,
                    SubscriptionReference = subRef,
                    Amount = inv.AmountDue / 100m,
                    Currency = (inv.Currency ?? "usd").ToUpperInvariant(),
                    FailureCode = inv.LastFinalizationError?.Code,
                    NextRetryAt = inv.NextPaymentAttempt
                };
            }
            case "payout.paid":
            {
                if (stripeEvent.Data.Object is not Payout p) return null;
                return new PayoutCompletedEvent
                {
                    GatewayReference = p.Id,
                    Status = PaymentStatus.Completed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = p.Id,
                    Amount = p.Amount / 100m,
                    Currency = (p.Currency ?? "usd").ToUpperInvariant(),
                    DestinationToken = p.DestinationId
                };
            }
            case "payout.failed":
            {
                if (stripeEvent.Data.Object is not Payout p) return null;
                return new PayoutFailedEvent
                {
                    GatewayReference = p.Id,
                    Status = PaymentStatus.Failed,
                    EventType = stripeEvent.Type,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = p.Id,
                    Amount = p.Amount / 100m,
                    Currency = (p.Currency ?? "usd").ToUpperInvariant(),
                    FailureCode = p.FailureCode,
                    FailureMessage = p.FailureMessage
                };
            }
            case "mandate.updated":
            {
                if (stripeEvent.Data.Object is not Mandate m) return null;
                if (m.Status?.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new MandateActivatedEvent
                    {
                        GatewayReference = m.Id,
                        Status = PaymentStatus.Completed,
                        EventType = stripeEvent.Type,
                        Category = WebhookEventCategory.MandateActivated,
                        MandateReference = m.Id
                    };
                }
                if (m.Status?.Equals("inactive", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return new MandateCancelledEvent
                    {
                        GatewayReference = m.Id,
                        Status = PaymentStatus.Cancelled,
                        EventType = stripeEvent.Type,
                        Category = WebhookEventCategory.MandateCancelled,
                        MandateReference = m.Id
                    };
                }
                return null;
            }
            default:
                return null;
        }
    }

    private static RequestOptions? BuildRequestOptions(string? idempotencyKey) =>
        string.IsNullOrEmpty(idempotencyKey) ? null : new RequestOptions { IdempotencyKey = idempotencyKey };

    private BhenguPaymentException TranslateException(StripeException ex, string operation)
    {
        var httpStatus = (int)ex.HttpStatusCode;
        var errorCode = ex.StripeError?.Code ?? ex.HttpStatusCode.ToString();
        var errorMessage = ex.StripeError?.Message ?? ex.Message;

        _logger.LogError(ex, "Stripe {Operation} failed: {HttpStatus} {Code} {Message}",
            operation, httpStatus, errorCode, errorMessage);

        if (httpStatus == 429)
            return new ProviderRateLimitException(ProviderName, providerErrorMessage: errorMessage, innerException: ex);

        if (httpStatus is >= 400 and < 500)
            return new PaymentDeclinedException(ProviderName, errorCode, errorMessage, ex);

        return new ProviderUnavailableException(ProviderName, $"HTTP {httpStatus}: {errorMessage}", ex);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "succeeded" or "paid" or "completed" => PaymentStatus.Completed,
        "processing" or "pending" or "requires_payment_method" or "requires_confirmation" or "requires_action" or "in_transit" => PaymentStatus.Pending,
        "canceled" or "cancelled" => PaymentStatus.Cancelled,
        "failed" => PaymentStatus.Failed,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private static string MapRefundReason(string reason) => reason?.ToLowerInvariant() switch
    {
        "duplicate" => "duplicate",
        "fraudulent" => "fraudulent",
        "requested_by_customer" => "requested_by_customer",
        _ => "requested_by_customer"
    };
}
