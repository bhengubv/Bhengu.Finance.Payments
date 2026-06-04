// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Kashier.Providers;

/// <summary>
/// Kashier (Egypt + UAE + KSA) payment gateway provider. Wraps the Kashier REST API and the
/// hosted-payment-page hash protocol. Implements <see cref="IPayoutProvider"/> because Kashier
/// exposes a /payouts endpoint for marketplace disbursements.
/// </summary>
/// <remarks>
/// 3DS is requested by setting the <c>"3ds"</c> field on the payment body — the provider
/// auto-sets that flag based on the metadata switch <c>request_3d_secure</c>; the explicit
/// step-up flow is in <see cref="KashierThreeDSecureProvider"/>.
/// </remarks>
public sealed class KashierPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;
    private readonly ILogger<KashierPaymentProvider> _logger;
    private readonly KashierIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Kashier;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.ThreeDSecure |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public KashierPaymentProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierPaymentProvider> logger,
        KashierIdempotencyCache idempotency)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.MerchantId)} is required");

        KashierHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Currency);
        var sw = Stopwatch.StartNew();

        var orderId = request.Metadata?.GetValueOrDefault("orderId") ?? $"kashier-{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();
        var requestThreeDs = !string.Equals(request.Metadata?.GetValueOrDefault("request_3d_secure"), "false", StringComparison.OrdinalIgnoreCase);

        var requestBody = new
        {
            amount,
            currency,
            shopperReference = request.Metadata?.GetValueOrDefault("shopperReference"),
            cardData = request.PaymentMethodToken,
            description = request.Description,
            // Kashier honours the "3ds" boolean. true = mandatory step-up if the issuer supports it.
            ThreeDs = requestThreeDs
        };

        try
        {
            var body = await KashierHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Post, $"orders/{Uri.EscapeDataString(orderId)}/payments",
                requestBody, "ProcessPayment", ct, request.IdempotencyKey).ConfigureAwait(false);
            var kashierResponse = JsonSerializer.Deserialize<KashierPaymentResponse>(body, KashierHttpClient.Json);

            _logger.LogInformation("Kashier charge created: order={OrderId} tx={Tx} status={Status}",
                orderId, kashierResponse?.Response?.TransactionId, kashierResponse?.Response?.Status);

            var txId = kashierResponse?.Response?.TransactionId ?? orderId;
            var status = MapStatus(kashierResponse?.Response?.Status);

            activity.SetOutcome(status switch
            {
                PaymentStatus.Completed => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                PaymentStatus.Failed => BhenguPaymentDiagnostics.Outcomes.Declined,
                _ => BhenguPaymentDiagnostics.Outcomes.Pending
            });
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", status.ToString().ToLowerInvariant()));

            return new PaymentResponse
            {
                GatewayReference = txId,
                Status = status,
                Amount = request.Amount,
                Currency = currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = kashierResponse?.Response?.RedirectUrl,
                Message = kashierResponse?.Response?.Status
            };
        }
        catch (Exception)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Error));
            throw;
        }
        finally
        {
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderName));
        }
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderName, request.GatewayReference);
        var requestBody = new
        {
            merchantId = _options.MerchantId,
            orderId = request.GatewayReference,
            transactionId = request.GatewayReference,
            amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            reason = request.Reason
        };

        var body = await KashierHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "payments/refund", requestBody, "ProcessRefund", ct, request.IdempotencyKey).ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<KashierPaymentResponse>(body, KashierHttpClient.Json);

        _logger.LogInformation("Kashier refund created: tx={Tx} status={Status}",
            request.GatewayReference, refundResponse?.Response?.Status);

        var mapped = MapStatus(refundResponse?.Response?.Status);
        var outcome = mapped is PaymentStatus.Completed or PaymentStatus.Refunded ? PaymentStatus.Refunded : mapped;

        BhenguPaymentDiagnostics.RefundsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("outcome", outcome == PaymentStatus.Refunded ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending));

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Response?.TransactionId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = outcome,
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Response?.Status
        };
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderName, request.Currency);
        var requestBody = new
        {
            merchantId = _options.MerchantId,
            destination = request.DestinationToken,
            amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            description = request.Description
        };

        var body = await KashierHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "payouts", requestBody, "ProcessPayout", ct, request.IdempotencyKey).ConfigureAwait(false);
        var payoutResponse = JsonSerializer.Deserialize<KashierPaymentResponse>(body, KashierHttpClient.Json);

        _logger.LogInformation("Kashier payout created: id={Id} status={Status}",
            payoutResponse?.Response?.TransactionId, payoutResponse?.Response?.Status);

        var status = MapStatus(payoutResponse?.Response?.Status);
        BhenguPaymentDiagnostics.PayoutsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("outcome", status == PaymentStatus.Completed ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending));

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.Response?.TransactionId ?? string.Empty,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = status,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.SecretKey : _options.WebhookSecret;
        bool valid;
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Kashier webhook secret not configured — signature verification cannot succeed.");
            valid = false;
        }
        else
        {
            try
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
                var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

                valid = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                    Encoding.UTF8.GetBytes(computedSignature));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kashier webhook signature verification raised");
                valid = false;
            }
        }

        BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("valid", valid));
        return valid;
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var webhook = JsonSerializer.Deserialize<KashierWebhookEvent>(payload, KashierHttpClient.Json);
            if (webhook is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Kashier webhook: event={Event} status={Status}",
                webhook.Event, webhook.Data?.Status);

            var data = webhook.Data;
            var reference = data?.TransactionId ?? data?.OrderId;
            if (string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            var amount = decimal.TryParse(data?.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ? amt : 0m;
            var currency = data?.Currency ?? _options.Currency;
            var eventUpper = webhook.Event?.ToUpperInvariant();
            var statusUpper = data?.Status?.ToUpperInvariant();

            return Task.FromResult<WebhookEvent?>(eventUpper switch
            {
                "PAY" or "CAPTURE" when statusUpper is "SUCCESS" or "PAID" => new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhook.Event,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = data?.ShopperReference,
                    PaymentMethodToken = data?.CardToken
                },
                "PAY" or "CAPTURE" => new ChargePendingEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Pending,
                    EventType = webhook.Event,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                },
                "FAILED" or "DECLINED" => new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = webhook.Event,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.Message
                },
                "REFUND" => new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhook.Event,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = data?.RefundId ?? reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                },
                "PAYOUT" when statusUpper is "SUCCESS" or "PAID" => new PayoutCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = webhook.Event,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = data?.Destination
                },
                "PAYOUT" => new PayoutFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = webhook.Event,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.Message
                },
                _ => new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = MapStatus(statusUpper),
                    EventType = webhook.Event,
                    Category = WebhookEventCategory.Unknown
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Kashier webhook");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    /// <summary>
    /// Build a hosted-payment-page URL for the redirect flow. Pure helper — exposed so consumers
    /// who prefer the hosted page over the server-to-server charge can build a signed redirect.
    /// </summary>
    public string BuildHostedPaymentUrl(string orderId, decimal amount, string? currency = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);
        var amt = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var ccy = string.IsNullOrWhiteSpace(currency) ? _options.Currency : currency.ToUpperInvariant();
        var mode = _options.UseSandbox ? "test" : (string.IsNullOrWhiteSpace(_options.Mode) ? "live" : _options.Mode);
        var hash = ComputeHostedPageHash(_options.MerchantId, orderId, amt, ccy, _options.SecretKey);

        var sb = new StringBuilder();
        sb.Append(_httpClient.BaseAddress?.ToString().TrimEnd('/') ?? KashierHttpClient.DefaultBaseUrl);
        sb.Append("/pay?merchantId=").Append(Uri.EscapeDataString(_options.MerchantId))
          .Append("&orderId=").Append(Uri.EscapeDataString(orderId))
          .Append("&amount=").Append(Uri.EscapeDataString(amt))
          .Append("&currency=").Append(Uri.EscapeDataString(ccy))
          .Append("&hash=").Append(hash)
          .Append("&mode=").Append(Uri.EscapeDataString(mode));
        if (!string.IsNullOrWhiteSpace(_options.RedirectUrl))
            sb.Append("&merchantRedirect=").Append(Uri.EscapeDataString(_options.RedirectUrl));
        if (!string.IsNullOrWhiteSpace(_options.ServerWebhookUrl))
            sb.Append("&serverWebhook=").Append(Uri.EscapeDataString(_options.ServerWebhookUrl));
        return sb.ToString();
    }

    internal static string ComputeHostedPageHash(
        string merchantId, string orderId, string amount, string currency, string secretKey)
    {
        var path = $"/?payment={merchantId}.{orderId}.{amount}.{currency}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey ?? string.Empty));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "SUCCESS" or "CAPTURED" or "PAID" or "COMPLETED" or "APPROVED" => PaymentStatus.Completed,
        "PENDING" or "PROCESSING" or "INPROGRESS" or "INITIATED" => PaymentStatus.Pending,
        "FAILED" or "DECLINED" or "REJECTED" => PaymentStatus.Failed,
        "CANCELED" or "CANCELLED" or "VOIDED" => PaymentStatus.Cancelled,
        "REFUNDED" or "PARTIAL_REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Kashier API response shapes (internal) ===

    private sealed class KashierPaymentResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("messages")] public KashierMessages? Messages { get; set; }
        [JsonPropertyName("response")] public KashierPaymentData? Response { get; set; }
    }

    private sealed class KashierMessages
    {
        [JsonPropertyName("en")] public string? En { get; set; }
    }

    private sealed class KashierPaymentData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("redirectUrl")] public string? RedirectUrl { get; set; }
    }

    private sealed class KashierWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public KashierWebhookData? Data { get; set; }
    }

    private sealed class KashierWebhookData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("shopperReference")] public string? ShopperReference { get; set; }
        [JsonPropertyName("cardToken")] public string? CardToken { get; set; }
        [JsonPropertyName("destination")] public string? Destination { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
