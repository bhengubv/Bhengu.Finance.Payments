// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Hubtel.Providers;

/// <summary>
/// Hubtel (Ghana) payment gateway provider. Wraps the Hubtel hosted-checkout, refund and
/// send-money (payout) APIs. Auth is HTTP Basic with ClientId:ClientSecret. Webhook signature
/// is HMAC-SHA256 (hex) over the raw body, keyed by WebhookSecret.
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class HubtelPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly HubtelOptions _options;
    private readonly IBhenguDistributedCache? _idempotencyCache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Hubtel;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the Hubtel payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public HubtelPaymentProvider(
        HttpClient httpClient,
        IOptions<HubtelOptions> options,
        ILogger<HubtelPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = idempotencyCache;

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantAccountNumber))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.MerchantAccountNumber)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-txnstatus.hubtel.com/"
                : _options.BaseUrl ?? "https://api-txnstatus.hubtel.com/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var body = new
        {
            totalAmount = request.Amount,
            description = request.Description,
            callbackUrl = _options.CallbackUrl,
            returnUrl = _options.ReturnUrl,
            merchantAccountNumber = _options.MerchantAccountNumber,
            cancellationUrl = _options.ReturnUrl,
            clientReference = request.PaymentMethodToken,
            payeeName = request.Metadata?.GetValueOrDefault("payeeName") ?? "",
            payeeMobileNumber = request.Metadata?.GetValueOrDefault("payeeMobileNumber") ?? "",
            payeeEmail = request.Metadata?.GetValueOrDefault("payeeEmail") ?? ""
        };

        var responseBody = await HubtelHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "checkout/initiate", body, ct, "ProcessPayment").ConfigureAwait(false);
        var checkout = JsonSerializer.Deserialize<HubtelCheckoutResponse>(responseBody);

        Logger.LogInformation("Hubtel checkout initiated: id={Id} url={Url}",
            checkout?.Data?.CheckoutId, checkout?.Data?.CheckoutUrl);

        var response = new PaymentResponse
        {
            GatewayReference = checkout?.Data?.CheckoutId ?? request.PaymentMethodToken,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = checkout?.Data?.CheckoutUrl,
            Message = checkout?.Message
        };

        await TrySetCachedAsync(request.IdempotencyKey, "charge", response, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var cached = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        var body = new
        {
            transactionId = request.GatewayReference,
            amount = request.Amount,
            reason = request.Reason,
            clientReference = request.IdempotencyKey ?? $"rf-{Guid.NewGuid():N}"
        };

        var responseBody = await HubtelHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "transactions/refund", body, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<HubtelRefundResponse>(responseBody);

        Logger.LogInformation("Hubtel refund: id={Id} status={Status}", refund?.Data?.TransactionId, refund?.Data?.Status);

        var response = new RefundResponse
        {
            GatewayReference = refund?.Data?.TransactionId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refund?.Data?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Message
        };

        await TrySetCachedAsync(request.IdempotencyKey, "refund", response, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        // DestinationToken format: "<channel>:<msisdn>" e.g. "mtn-gh:233244000000"
        var colon = request.DestinationToken.IndexOf(':');
        if (colon <= 0)
            throw new BhenguPaymentException(ProviderName,
                "Hubtel PayoutRequest.DestinationToken must be '<channel>:<msisdn>' (channel one of mtn-gh|vodafone-gh|tigo-gh)");

        var channel = request.DestinationToken[..colon];
        var msisdn = request.DestinationToken[(colon + 1)..];
        var clientReference = request.IdempotencyKey ?? $"po-{Guid.NewGuid():N}";

        var body = new
        {
            RecipientName = request.Description,
            RecipientMsisdn = msisdn,
            CustomerEmail = "",
            Channel = channel,
            Amount = request.Amount,
            PrimaryCallbackUrl = _options.CallbackUrl,
            Description = request.Description,
            ClientReference = clientReference
        };

        var path = $"merchantaccount/merchants/{Uri.EscapeDataString(_options.MerchantAccountNumber)}/send/mobilemoney";
        var responseBody = await HubtelHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, path, body, ct, "ProcessPayout").ConfigureAwait(false);
        var payout = JsonSerializer.Deserialize<HubtelPayoutResponse>(responseBody);

        Logger.LogInformation("Hubtel send-money payout: id={Id} status={Status} channel={Channel}",
            payout?.Data?.TransactionId, payout?.Data?.TransactionStatus, channel);

        var response = new PayoutResponse
        {
            GatewayReference = payout?.Data?.TransactionId ?? clientReference,
            Status = MapStatus(payout?.Data?.TransactionStatus ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };

        await TrySetCachedAsync(request.IdempotencyKey, "payout", response, ct).ConfigureAwait(false);
        return response;
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            Logger.LogWarning("Hubtel WebhookSecret not configured — signature verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        return RunWebhookVerify(() => SignatureHelpers.VerifyHmacSha256(payload, signature, _options.WebhookSecret));
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
            var evt = JsonSerializer.Deserialize<HubtelWebhookEvent>(payload);
            var data = evt?.Data;
            if (data is null || string.IsNullOrEmpty(data.ClientReference ?? data.TransactionId))
                return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Hubtel webhook event: type={Type} status={Status}", evt?.Type, data.Status);

            var reference = data.ClientReference ?? data.TransactionId!;
            var typedEvent = MapToTypedEvent(evt?.Type, data, reference);
            return Task.FromResult(typedEvent);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Hubtel webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapToTypedEvent(string? type, HubtelWebhookData data, string reference)
    {
        var statusLower = data.Status?.ToLowerInvariant();
        var typeLower = type?.ToLowerInvariant();
        var currency = data.Currency ?? "GHS";

        return (typeLower, statusLower) switch
        {
            ("refund.completed", _) or (_, "refunded") => new RefundSucceededEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Refunded,
                EventType = type ?? data.Status,
                Category = WebhookEventCategory.RefundSucceeded,
                RefundReference = data.TransactionId ?? reference,
                Amount = data.Amount,
                Currency = currency,
                IsPartial = false
            },
            ("payout.completed", _) => new PayoutCompletedEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Completed,
                EventType = type ?? data.Status,
                Category = WebhookEventCategory.PayoutCompleted,
                PayoutReference = data.TransactionId ?? reference,
                Amount = data.Amount,
                Currency = currency
            },
            ("payout.failed", _) => new PayoutFailedEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Failed,
                EventType = type ?? data.Status,
                Category = WebhookEventCategory.PayoutFailed,
                PayoutReference = data.TransactionId ?? reference,
                Amount = data.Amount,
                Currency = currency,
                FailureMessage = data.Status
            },
            (_, "success") or (_, "paid") or (_, "completed") => new ChargeSucceededEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Completed,
                EventType = type ?? data.Status,
                Category = WebhookEventCategory.ChargeSucceeded,
                Amount = data.Amount,
                Currency = currency
            },
            (_, "failed") or (_, "declined") => new ChargeFailedEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Failed,
                EventType = type ?? data.Status,
                Category = WebhookEventCategory.ChargeFailed,
                Amount = data.Amount,
                Currency = currency,
                FailureMessage = data.Status
            },
            (_, "cancelled") or (_, "canceled") => new WebhookEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Cancelled,
                EventType = type ?? data.Status,
                Category = WebhookEventCategory.Unknown
            },
            (_, "pending") or (_, "processing") => new ChargePendingEvent
            {
                GatewayReference = reference,
                Status = PaymentStatus.Pending,
                EventType = type ?? data.Status,
                Category = WebhookEventCategory.ChargePending,
                Amount = data.Amount,
                Currency = currency
            },
            _ => null
        };
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"hubtel:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"hubtel:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "paid" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private sealed class HubtelCheckoutResponse
    {
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelCheckoutData? Data { get; set; }
    }

    private sealed class HubtelCheckoutData
    {
        [JsonPropertyName("checkoutUrl")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("checkoutId")] public string? CheckoutId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
    }

    private sealed class HubtelRefundResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelRefundData? Data { get; set; }
    }

    private sealed class HubtelRefundData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class HubtelPayoutResponse
    {
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelPayoutData? Data { get; set; }
    }

    private sealed class HubtelPayoutData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
        [JsonPropertyName("transactionStatus")] public string? TransactionStatus { get; set; }
    }

    private sealed class HubtelWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("data")] public HubtelWebhookData? Data { get; set; }
    }

    private sealed class HubtelWebhookData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}
