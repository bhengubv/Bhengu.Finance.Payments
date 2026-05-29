// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
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
    private readonly HttpClient _httpClient;
    private readonly StripeOptions _options;
    private readonly ILogger<StripePaymentProvider> _logger;

    public string ProviderName => "stripe";

    public StripePaymentProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripePaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StripeOptions.SecretKey)} is required");

        StripeConfiguration.ApiKey = _options.SecretKey;
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
            var service = new PaymentIntentService();
            var paymentIntent = await service.CreateAsync(options, cancellationToken: ct).ConfigureAwait(false);

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
            var service = new PayoutService();
            var payout = await service.CreateAsync(options, cancellationToken: ct).ConfigureAwait(false);

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
            var service = new RefundService();
            var refund = await service.CreateAsync(options, cancellationToken: ct).ConfigureAwait(false);

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
            var stripeEvent = EventUtility.ParseEvent(payload);
            _logger.LogInformation("Parsed Stripe webhook event: {EventType}", stripeEvent.Type);

            string? gatewayReference = null;
            PaymentStatus status;

            switch (stripeEvent.Type)
            {
                case "payment_intent.succeeded":
                    gatewayReference = (stripeEvent.Data.Object as PaymentIntent)?.Id;
                    status = PaymentStatus.Completed;
                    break;
                case "payment_intent.payment_failed":
                    gatewayReference = (stripeEvent.Data.Object as PaymentIntent)?.Id;
                    status = PaymentStatus.Failed;
                    break;
                case "payment_intent.canceled":
                    gatewayReference = (stripeEvent.Data.Object as PaymentIntent)?.Id;
                    status = PaymentStatus.Cancelled;
                    break;
                case "charge.refunded":
                    gatewayReference = (stripeEvent.Data.Object as Charge)?.PaymentIntentId
                        ?? (stripeEvent.Data.Object as Charge)?.Id;
                    status = PaymentStatus.Refunded;
                    break;
                default:
                    return Task.FromResult<WebhookEvent?>(null);
            }

            if (string.IsNullOrEmpty(gatewayReference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = gatewayReference,
                Status = status,
                EventType = stripeEvent.Type
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Stripe webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

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
