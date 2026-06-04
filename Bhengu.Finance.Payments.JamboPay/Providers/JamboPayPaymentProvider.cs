// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.JamboPay.Providers;

/// <summary>
/// JamboPay (Kenya) payment gateway provider. Wraps JamboPay v1.
/// Auth is dual: static x-api-key header PLUS short-lived Bearer token from
/// /oauth/token (client_credentials). Supports collections, refunds and payouts;
/// webhook signature is HMAC-SHA256 (hex) over the raw body keyed by WebhookSecret.
/// </summary>
public sealed class JamboPayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly JamboPayOptions _options;
    private readonly IBhenguDistributedCache? _idempotencyCache;
    private string? _cachedToken;
    private DateTime _tokenExpiresAtUtc;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.JamboPay;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the JamboPay payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public JamboPayPaymentProvider(
        HttpClient httpClient,
        IOptions<JamboPayOptions> options,
        ILogger<JamboPayPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = idempotencyCache;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.MerchantCode)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.BaseUrl ?? "https://api.jambopay.com/v1/";
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

            var body = new
            {
                merchant_code = _options.MerchantCode,
                transaction_ref = request.PaymentMethodToken,
                amount = request.Amount,
                currency = request.Currency.ToUpperInvariant(),
                customer = new
                {
                    email = request.Metadata?.GetValueOrDefault("email") ?? "",
                    msisdn = request.Metadata?.GetValueOrDefault("msisdn") ?? "",
                    name = request.Metadata?.GetValueOrDefault("name") ?? ""
                },
                payment_method = request.Metadata?.GetValueOrDefault("payment_method") ?? "CARD",
                callback_url = _options.CallbackUrl,
                idempotency_key = request.IdempotencyKey
            };

            var responseBody = await SendAsync(HttpMethod.Post, "payments/initiate", body, ct, "ProcessPayment", request.IdempotencyKey).ConfigureAwait(false);
            var payment = JsonSerializer.Deserialize<JamboPayInitiateResponse>(responseBody);

            Logger.LogInformation("JamboPay payment initiated: ref={Ref} status={Status} url={Url}",
                request.PaymentMethodToken, payment?.Status, payment?.CheckoutUrl);

            var response = new PaymentResponse
            {
                GatewayReference = payment?.TransactionRef ?? request.PaymentMethodToken,
                Status = MapStatus(payment?.Status ?? "pending"),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = payment?.CheckoutUrl,
                Message = payment?.Message
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
                transaction_ref = request.GatewayReference,
                amount = request.Amount,
                reason = request.Reason,
                idempotency_key = request.IdempotencyKey
            };

            var responseBody = await SendAsync(HttpMethod.Post, "payments/refund", body, ct, "ProcessRefund", request.IdempotencyKey).ConfigureAwait(false);
            var refund = JsonSerializer.Deserialize<JamboPayRefundResponse>(responseBody);

            Logger.LogInformation("JamboPay refund: id={Id} status={Status} for {Ref}",
                refund?.RefundId, refund?.Status, request.GatewayReference);

            var response = new RefundResponse
            {
                GatewayReference = refund?.RefundId ?? request.GatewayReference,
                Amount = request.Amount,
                Status = MapStatus(refund?.Status ?? "pending"),
                ProcessedAt = DateTime.UtcNow,
                Message = refund?.Message
            };

            await TrySetCachedAsync(request.IdempotencyKey, "refund", response, ct).ConfigureAwait(false);
            return response;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            // DestinationToken layouts:
            //   "msisdn:254700000000"          → mobile-money payout
            //   "bank:KCBLKENX:1234567890"     → bank payout (bankCode + account)
            var token = request.DestinationToken;
            object beneficiary;
            if (token.StartsWith("msisdn:", StringComparison.OrdinalIgnoreCase))
                beneficiary = new { msisdn = token["msisdn:".Length..] };
            else if (token.StartsWith("bank:", StringComparison.OrdinalIgnoreCase))
            {
                var rest = token["bank:".Length..];
                var colon = rest.IndexOf(':');
                if (colon <= 0)
                    throw new BhenguPaymentException(ProviderName,
                        "JamboPay PayoutRequest.DestinationToken bank form must be 'bank:<bankCode>:<accountNumber>'");
                beneficiary = new { bank_code = rest[..colon], account_number = rest[(colon + 1)..] };
            }
            else
                throw new BhenguPaymentException(ProviderName,
                    "JamboPay PayoutRequest.DestinationToken must be 'msisdn:<phone>' or 'bank:<code>:<account>'");

            var body = new
            {
                merchant_code = _options.MerchantCode,
                beneficiary,
                amount = request.Amount,
                currency = request.Currency.ToUpperInvariant(),
                narration = request.Description,
                idempotency_key = request.IdempotencyKey
            };

            var responseBody = await SendAsync(HttpMethod.Post, "payouts/initiate", body, ct, "ProcessPayout", request.IdempotencyKey).ConfigureAwait(false);
            var payout = JsonSerializer.Deserialize<JamboPayPayoutResponse>(responseBody);

            Logger.LogInformation("JamboPay payout initiated: id={Id} status={Status}", payout?.PayoutId, payout?.Status);

            var response = new PayoutResponse
            {
                GatewayReference = payout?.PayoutId ?? string.Empty,
                Status = MapStatus(payout?.Status ?? "pending"),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", response, ct).ConfigureAwait(false);
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
            if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            {
                Logger.LogWarning("JamboPay WebhookSecret not configured — signature verification cannot succeed.");
                return false;
            }

            try
            {
                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var hex = Convert.ToHexString(hash).ToLowerInvariant();
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                    Encoding.UTF8.GetBytes(hex));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "JamboPay webhook signature verification raised");
                return false;
            }
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
                var evt = JsonSerializer.Deserialize<JamboPayWebhookEvent>(payload);
                if (evt is null || string.IsNullOrEmpty(evt.TransactionRef))
                    return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed JamboPay webhook event: {EventType}", evt.EventType);

                var eventLower = evt.EventType?.ToLowerInvariant();
                var currency = evt.Currency ?? _options.Currency;
                WebhookEvent? typed = eventLower switch
                {
                    "payment.completed" or "payment.success" => new ChargeSucceededEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Completed,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.ChargeSucceeded,
                        Amount = evt.Amount,
                        Currency = currency
                    },
                    "payment.failed" => new ChargeFailedEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Failed,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = evt.Amount,
                        Currency = currency,
                        FailureCode = evt.Status,
                        FailureMessage = evt.Message
                    },
                    "payment.pending" => new ChargePendingEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Pending,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.ChargePending,
                        Amount = evt.Amount,
                        Currency = currency
                    },
                    "payment.cancelled" => new WebhookEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Cancelled,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.Unknown
                    },
                    "refund.completed" or "refund.success" => new RefundSucceededEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Refunded,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.RefundSucceeded,
                        RefundReference = evt.RefundId ?? evt.TransactionRef,
                        Amount = evt.Amount,
                        Currency = currency,
                        IsPartial = false
                    },
                    "refund.failed" => new RefundFailedEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Failed,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.RefundFailed,
                        Amount = evt.Amount,
                        Currency = currency,
                        FailureCode = evt.Status,
                        FailureMessage = evt.Message
                    },
                    "payout.completed" or "payout.success" => new PayoutCompletedEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Completed,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = evt.TransactionRef,
                        Amount = evt.Amount,
                        Currency = currency
                    },
                    "payout.failed" => new PayoutFailedEvent
                    {
                        GatewayReference = evt.TransactionRef,
                        Status = PaymentStatus.Failed,
                        EventType = evt.EventType,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = evt.TransactionRef,
                        Amount = evt.Amount,
                        Currency = currency,
                        FailureCode = evt.Status,
                        FailureMessage = evt.Message
                    },
                    _ => null
                };

                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse JamboPay webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiresAtUtc)
            return _cachedToken!;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiresAtUtc)
                return _cachedToken!;

            using var req = new HttpRequestMessage(HttpMethod.Post, "oauth/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ClientId,
                    ["client_secret"] = _options.ClientSecret
                })
            };
            req.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "HTTP request to JamboPay failed", ex);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
                throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
            }

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is >= 400 and < 500)
                    throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
                throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
            }

            var auth = JsonSerializer.Deserialize<JamboPayAuthResponse>(responseBody);
            if (string.IsNullOrWhiteSpace(auth?.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "JamboPay /oauth/token returned an empty access_token");

            _cachedToken = auth!.AccessToken;
            var ttlSeconds = auth.ExpiresIn > 30 ? auth.ExpiresIn - 30 : 60;
            _tokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(ttlSeconds);
            return _cachedToken!;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation, string? idempotencyKey = null)
    {
        var token = await EnsureTokenAsync(ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to JamboPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("JamboPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"jambopay:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"jambopay:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "successful" or "succeeded" or "success" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private sealed class JamboPayAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class JamboPayInitiateResponse
    {
        [JsonPropertyName("transaction_ref")] public string? TransactionRef { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("checkout_url")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class JamboPayRefundResponse
    {
        [JsonPropertyName("refund_id")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class JamboPayPayoutResponse
    {
        [JsonPropertyName("payout_id")] public string? PayoutId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class JamboPayWebhookEvent
    {
        [JsonPropertyName("event")] public string? EventType { get; set; }
        [JsonPropertyName("transaction_ref")] public string? TransactionRef { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("refund_id")] public string? RefundId { get; set; }
    }
}
