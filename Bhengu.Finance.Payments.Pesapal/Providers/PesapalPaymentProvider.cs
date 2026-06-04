// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Pesapal.Providers;

/// <summary>
/// Pesapal (Kenya / East Africa) payment gateway provider. Wraps Pesapal API 3.0.
/// Auth is a short-lived Bearer token (5-min TTL) obtained via /api/Auth/RequestToken.
/// Webhook IPNs are NOT signed by Pesapal — <see cref="VerifyWebhookSignature"/> returns true
/// when payload and signature are non-empty and the configured ConsumerSecret matches the
/// provided signature; production callers should ALSO call /api/Transactions/GetTransactionStatus
/// using the OrderTrackingId to confirm settlement (the canonical Pesapal hardening).
/// </summary>
public sealed class PesapalPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly PesapalOptions _options;
    private readonly IBhenguDistributedCache? _idempotencyCache;
    private readonly PesapalTokenCache _tokenCache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Pesapal;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the Pesapal payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public PesapalPaymentProvider(
        HttpClient httpClient,
        IOptions<PesapalOptions> options,
        ILogger<PesapalPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null,
        PesapalTokenCache? tokenCache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = idempotencyCache;
        _tokenCache = tokenCache ?? new PesapalTokenCache();

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var liveDefault = _options.BaseUrl ?? "https://pay.pesapal.com/v3";
            var sandboxDefault = _options.SandboxUrl ?? "https://cybqa.pesapal.com/pesapalv3";
            var raw = _options.UseSandbox ? sandboxDefault : liveDefault;
            // Always end with a trailing slash so relative paths resolve cleanly.
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            if (string.IsNullOrWhiteSpace(_options.IpnId))
                throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.IpnId)} is required (register an IPN via /api/URLSetup/RegisterIPN first)");

            var email = request.Metadata?.GetValueOrDefault("email") ?? "";
            var phone = request.Metadata?.GetValueOrDefault("phone_number") ?? "";
            var country = request.Metadata?.GetValueOrDefault("country_code") ?? "KE";
            var firstName = request.Metadata?.GetValueOrDefault("first_name") ?? "";
            var lastName = request.Metadata?.GetValueOrDefault("last_name") ?? "";

            var body = new
            {
                id = request.PaymentMethodToken,
                currency = request.Currency.ToUpperInvariant(),
                amount = request.Amount,
                description = request.Description,
                callback_url = _options.CallbackUrl,
                notification_id = _options.IpnId,
                billing_address = new
                {
                    email_address = email,
                    phone_number = phone,
                    country_code = country,
                    first_name = firstName,
                    last_name = lastName
                }
            };

            var responseBody = await SendAsync(HttpMethod.Post, "api/Transactions/SubmitOrderRequest", body, ct, "ProcessPayment").ConfigureAwait(false);
            var submit = JsonSerializer.Deserialize<PesapalSubmitOrderResponse>(responseBody);

            Logger.LogInformation("Pesapal order submitted: tracking={Tracking} merchantRef={MerchantRef}",
                submit?.OrderTrackingId, submit?.MerchantReference);

            var response = new PaymentResponse
            {
                GatewayReference = submit?.OrderTrackingId ?? request.PaymentMethodToken,
                Status = PaymentStatus.Pending,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = submit?.RedirectUrl,
                Message = submit?.Status
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", response, ct).ConfigureAwait(false);
            return response;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, async () =>
        {
            var cached = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var body = new
            {
                confirmation_code = request.GatewayReference,
                amount = request.Amount,
                username = _options.ConsumerKey,
                remarks = request.Reason
            };

            var responseBody = await SendAsync(HttpMethod.Post, "api/Transactions/RefundRequest", body, ct, "ProcessRefund").ConfigureAwait(false);
            var refund = JsonSerializer.Deserialize<PesapalRefundResponse>(responseBody);

            Logger.LogInformation("Pesapal refund: status={Status} message={Message}", refund?.Status, refund?.Message);

            var mapped = string.Equals(refund?.Status, "200", StringComparison.Ordinal)
                ? PaymentStatus.Refunded
                : PaymentStatus.Failed;

            var response = new RefundResponse
            {
                GatewayReference = request.GatewayReference,
                Amount = request.Amount,
                Status = mapped,
                ProcessedAt = DateTime.UtcNow,
                Message = refund?.Message
            };

            await TrySetCachedAsync(request.IdempotencyKey, "refund", response, ct).ConfigureAwait(false);
            return response;
        }, ct);
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            {
                Logger.LogWarning("Pesapal ConsumerSecret not configured — IPN verification cannot proceed.");
                return false;
            }

            // Pesapal does NOT HMAC IPN payloads. We perform a constant-time equality check between
            // the supplied signature and the configured ConsumerSecret so callers MUST send a known
            // shared secret in their reverse-proxy header. Production callers should additionally call
            // GetTransactionStatus(OrderTrackingId) for canonical confirmation.
            var a = Encoding.UTF8.GetBytes(signature);
            var b = Encoding.UTF8.GetBytes(_options.ConsumerSecret);
            return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () =>
        {
            try
            {
                var ipn = JsonSerializer.Deserialize<PesapalIpnEvent>(payload);
                if (ipn is null || string.IsNullOrEmpty(ipn.OrderTrackingId))
                    return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Pesapal IPN: tracking={Tracking} notifType={NotifType}",
                    ipn.OrderTrackingId, ipn.OrderNotificationType);

                var notif = ipn.OrderNotificationType?.ToUpperInvariant();
                // Pesapal IPNs always carry an OrderNotificationType that we map to a ChargePendingEvent —
                // the caller must query GetTransactionStatus(OrderTrackingId) to learn the final status.
                // We surface a ChargePending typed event so consumers can route on category.
                WebhookEvent typed = notif switch
                {
                    "IPNCHANGE" or "CHANGE" or "COMPLETED" => new ChargePendingEvent
                    {
                        GatewayReference = ipn.OrderTrackingId,
                        Status = PaymentStatus.Pending,
                        EventType = ipn.OrderNotificationType,
                        Category = WebhookEventCategory.ChargePending,
                        Amount = 0m,
                        Currency = _options.Currency
                    },
                    _ => new ChargePendingEvent
                    {
                        GatewayReference = ipn.OrderTrackingId,
                        Status = PaymentStatus.Pending,
                        EventType = ipn.OrderNotificationType ?? "unknown",
                        Category = WebhookEventCategory.ChargePending,
                        Amount = 0m,
                        Currency = _options.Currency
                    }
                };

                return Task.FromResult<WebhookEvent?>(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Pesapal IPN");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"pesapal:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"pesapal:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var token = await PesapalHttpClient.EnsureTokenAsync(_httpClient, Logger, _options, _tokenCache, ct).ConfigureAwait(false);
        return await PesapalHttpClient.SendAsync(_httpClient, Logger, method, path, body, token, ct, operation).ConfigureAwait(false);
    }

    // === Pesapal API response shapes (internal) ===

    private sealed class PesapalSubmitOrderResponse
    {
        [JsonPropertyName("order_tracking_id")] public string? OrderTrackingId { get; set; }
        [JsonPropertyName("merchant_reference")] public string? MerchantReference { get; set; }
        [JsonPropertyName("redirect_url")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class PesapalRefundResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class PesapalIpnEvent
    {
        [JsonPropertyName("OrderTrackingId")] public string? OrderTrackingId { get; set; }
        [JsonPropertyName("OrderMerchantReference")] public string? OrderMerchantReference { get; set; }
        [JsonPropertyName("OrderNotificationType")] public string? OrderNotificationType { get; set; }
    }
}
