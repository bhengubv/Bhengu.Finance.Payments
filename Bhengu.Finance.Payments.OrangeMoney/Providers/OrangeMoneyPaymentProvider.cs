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
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.OrangeMoney.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OrangeMoney.Providers;

/// <summary>
/// Orange Money Web Payment provider. Checkout-redirect flow: the SDK creates a payment session and
/// returns the hosted payment URL in <see cref="PaymentResponse.RedirectUrl"/>; the caller redirects the payer
/// to that URL.
/// <para>
/// <b>Refund note:</b> The Orange Money Web Payment API has no automated refund endpoint.
/// <see cref="ProcessRefundAsync"/> throws <see cref="BhenguPaymentException"/> directing the caller to
/// process the reversal manually via the Orange Money merchant portal.
/// </para>
/// <para>
/// <b>Payouts:</b> Implemented via the Orange Money B2C cash-in API
/// (<c>/orange-money-b2c/{country}/v1/cashin</c>) — see <see cref="ProcessPayoutAsync"/>.
/// </para>
/// <para>
/// <b>Webhook signature note:</b> Orange Money's notif callback does not carry a cryptographic signature.
/// Verification is performed by calling the <c>transactionstatus</c> endpoint with the <c>notif_token</c>
/// received in the payment-creation response. <see cref="VerifyWebhookSignature"/> compares the supplied
/// signature against the cached notif_token; callers must persist that token alongside their order record.
/// </para>
/// </summary>
public sealed class OrangeMoneyPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly OrangeMoneyOptions _options;
    private readonly IBhenguDistributedCache? _idempotencyCache;
    private readonly string _baseUrl;

    private string? _cachedAccessToken;
    private DateTime _cachedTokenExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.OrangeMoney;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the Orange Money payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public OrangeMoneyPaymentProvider(
        HttpClient httpClient,
        IOptions<OrangeMoneyOptions> options,
        ILogger<OrangeMoneyPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = idempotencyCache;

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.ConsumerSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Country))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.Country)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://api.orange.com/")
            : (_options.BaseUrl ?? "https://api.orange.com/");
        if (!_baseUrl.EndsWith('/')) _baseUrl += "/";

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    /// <inheritdoc/>
    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Currency);
        var started = DateTime.UtcNow;
        var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
        if (cached is not null)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return cached;
        }

        try
        {
            var orderId = request.Metadata?.TryGetValue("order_id", out var oid) == true
                ? oid
                : (request.IdempotencyKey is { Length: > 0 } k ? k[..Math.Min(20, k.Length)] : Guid.NewGuid().ToString("N")[..20]);
            var lang = request.Metadata?.TryGetValue("lang", out var l) == true ? l : "fr";

            var body = new
            {
                merchant_key = _options.MerchantKey,
                currency = request.Currency.ToUpperInvariant(),
                order_id = orderId,
                amount = (int)Math.Round(request.Amount, MidpointRounding.AwayFromZero),
                return_url = _options.ReturnUrl,
                cancel_url = _options.CancelUrl,
                notif_url = _options.NotifUrl,
                lang,
                reference = request.Description
            };

            var (responseBody, _) = await SendAsync(
                HttpMethod.Post, $"orange-money-webpay/{_options.Country}/v1/webpayment", body, ct, "Webpayment").ConfigureAwait(false);

            var wp = JsonSerializer.Deserialize<OrangeWebPaymentResponse>(responseBody);

            Logger.LogInformation(
                "Orange Money web payment created: OrderId={OrderId} PayToken={PayToken} Status={Status}",
                orderId, wp?.PayToken, wp?.Status);

            // payment_url is returned to the caller via RedirectUrl — they redirect the payer to it.
            var response = new PaymentResponse
            {
                GatewayReference = wp?.PayToken ?? orderId,
                Status = MapStatus(wp?.Status),
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = wp?.PaymentUrl,
                Message = wp?.Message ?? wp?.Status
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", response, ct).ConfigureAwait(false);
            var outcome = response.Status switch
            {
                PaymentStatus.Completed => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                _ => BhenguPaymentDiagnostics.Outcomes.Declined
            };
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcome));
            activity.SetOutcome(outcome);
            return response;
        }
        catch (Exception ex)
        {
            var outcome = ClassifyOutcome(ex);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcome));
            activity.SetOutcome(outcome);
            throw;
        }
        finally
        {
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(
                (DateTime.UtcNow - started).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderName));
        }
    }

    /// <summary>
    /// Orange Money Web Payment has no automated refund endpoint. Process the reversal manually via the
    /// Orange Money merchant portal.
    /// </summary>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "Orange Money Web Payment has no automated refund API. Reverse the transaction via the Orange Money merchant portal.");
    }

    /// <summary>
    /// Pay funds out via the Orange Money B2C cash-in API
    /// (<c>/orange-money-b2c/{country}/v1/cashin</c>). The <see cref="PayoutRequest.DestinationToken"/>
    /// must be the recipient's Orange Money MSISDN (with country prefix, e.g. <c>225XXXXXXXX</c>).
    /// </summary>
    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderName, request.Currency);
        var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
        if (cached is not null)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return cached;
        }

        try
        {
            var orderId = request.IdempotencyKey is { Length: > 0 } k
                ? k[..Math.Min(20, k.Length)]
                : Guid.NewGuid().ToString("N")[..20];

            var body = new
            {
                merchant_key = _options.MerchantKey,
                currency = request.Currency.ToUpperInvariant(),
                order_id = orderId,
                amount = (int)Math.Round(request.Amount, MidpointRounding.AwayFromZero),
                subscriber_msisdn = request.DestinationToken.TrimStart('+'),
                notif_url = _options.NotifUrl,
                reference = request.Description
            };

            var (responseBody, _) = await SendAsync(
                HttpMethod.Post, $"orange-money-b2c/{_options.Country}/v1/cashin", body, ct, "Cashin").ConfigureAwait(false);

            var b2c = JsonSerializer.Deserialize<OrangeB2cResponse>(responseBody);

            Logger.LogInformation(
                "Orange Money B2C cashin created: OrderId={OrderId} TxnId={TxnId} Status={Status}",
                orderId, b2c?.TxnId, b2c?.Status);

            var status = MapStatus(b2c?.Status);
            var response = new PayoutResponse
            {
                GatewayReference = b2c?.TxnId ?? orderId,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", response, ct).ConfigureAwait(false);
            var outcome = status switch
            {
                PaymentStatus.Completed => BhenguPaymentDiagnostics.Outcomes.Success,
                PaymentStatus.Pending => BhenguPaymentDiagnostics.Outcomes.Pending,
                PaymentStatus.Failed => BhenguPaymentDiagnostics.Outcomes.Declined,
                _ => BhenguPaymentDiagnostics.Outcomes.Success
            };
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcome));
            activity.SetOutcome(outcome);
            return response;
        }
        catch (Exception ex)
        {
            var outcome = ClassifyOutcome(ex);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcome));
            activity.SetOutcome(outcome);
            throw;
        }
    }

    /// <summary>
    /// Orange Money does NOT cryptographically sign callbacks. Pass the persisted <c>notif_token</c>
    /// (returned by <see cref="ProcessPaymentAsync"/> as part of the underlying API response) as
    /// <paramref name="signature"/>. The payload's <c>notif_token</c> field is compared in constant time.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        bool valid = false;
        try
        {
            var evt = JsonSerializer.Deserialize<OrangeNotifPayload>(payload);
            var notifToken = evt?.NotifToken;
            if (string.IsNullOrEmpty(notifToken))
            {
                Logger.LogWarning("Orange Money notif payload missing notif_token — cannot verify.");
                valid = false;
            }
            else
            {
                valid = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(notifToken),
                    Encoding.UTF8.GetBytes(signature));
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Orange Money notif token verification raised");
            valid = false;
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
            var evt = JsonSerializer.Deserialize<OrangeNotifPayload>(payload);
            if (evt is null || string.IsNullOrEmpty(evt.PayToken))
                return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation(
                "Parsed Orange Money notif: PayToken={PayToken} Status={Status}",
                evt.PayToken, evt.Status);

            var status = MapStatus(evt.Status);
            decimal.TryParse(evt.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount);
            var currency = evt.Currency ?? "XOF";
            var statusLower = evt.Status?.ToUpperInvariant();
            var notifType = evt.NotificationType?.ToLowerInvariant();

            WebhookEvent? typed = (notifType, statusLower) switch
            {
                ("cashin", "SUCCESS") or ("cashin", "SUCCESSFUL") or ("cashin", "COMPLETED") => new PayoutCompletedEvent
                {
                    GatewayReference = evt.PayToken,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Status,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = evt.PayToken,
                    Amount = amount,
                    Currency = currency
                },
                ("cashin", _) when status == PaymentStatus.Failed => new PayoutFailedEvent
                {
                    GatewayReference = evt.PayToken,
                    Status = PaymentStatus.Failed,
                    EventType = evt.Status,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = evt.PayToken,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = evt.Status
                },
                _ => MapChargeStatus(evt, status, amount, currency)
            };

            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Orange Money notif payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static WebhookEvent? MapChargeStatus(OrangeNotifPayload evt, PaymentStatus status, decimal amount, string currency)
    {
        var eventType = (evt.Status ?? "unknown").ToLowerInvariant();
        return status switch
        {
            PaymentStatus.Completed => new ChargeSucceededEvent
            {
                GatewayReference = evt.PayToken!,
                Status = PaymentStatus.Completed,
                EventType = eventType,
                Category = WebhookEventCategory.ChargeSucceeded,
                Amount = amount,
                Currency = currency
            },
            PaymentStatus.Pending => new ChargePendingEvent
            {
                GatewayReference = evt.PayToken!,
                Status = PaymentStatus.Pending,
                EventType = eventType,
                Category = WebhookEventCategory.ChargePending,
                Amount = amount,
                Currency = currency
            },
            PaymentStatus.Failed => new ChargeFailedEvent
            {
                GatewayReference = evt.PayToken!,
                Status = PaymentStatus.Failed,
                EventType = eventType,
                Category = WebhookEventCategory.ChargeFailed,
                Amount = amount,
                Currency = currency,
                FailureCode = evt.Status
            },
            PaymentStatus.Cancelled => new WebhookEvent
            {
                GatewayReference = evt.PayToken!,
                Status = PaymentStatus.Cancelled,
                EventType = eventType,
                Category = WebhookEventCategory.Unknown
            },
            _ => null
        };
    }

    // ===== HTTP plumbing =====

    private async Task<(string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, $"HTTP request to Orange Money ({operation}) failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Orange Money {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return (responseBody, response);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedAccessToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            return _cachedAccessToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedAccessToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
                return _cachedAccessToken;

            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ConsumerKey}:{_options.ConsumerSecret}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, "oauth/v2/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "client_credentials")
                })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "Orange Money OAuth call failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Orange Money OAuth failed: {StatusCode} {Body}", response.StatusCode, body);
                throw new ProviderUnavailableException(ProviderName, $"Orange Money OAuth HTTP {(int)response.StatusCode}: {body}");
            }

            var token = JsonSerializer.Deserialize<OrangeOAuthResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "Orange Money OAuth returned an empty token");

            _cachedAccessToken = token.AccessToken;
            _cachedTokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3599);
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"orangemoney:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"orangemoney:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static string ClassifyOutcome(Exception ex) => ex switch
    {
        PaymentDeclinedException => BhenguPaymentDiagnostics.Outcomes.Declined,
        ProviderRateLimitException => BhenguPaymentDiagnostics.Outcomes.RateLimited,
        ProviderUnavailableException => BhenguPaymentDiagnostics.Outcomes.Unavailable,
        _ => BhenguPaymentDiagnostics.Outcomes.Error
    };

    private static PaymentStatus MapStatus(string? raw) => (raw ?? string.Empty).ToUpperInvariant() switch
    {
        "SUCCESS" or "SUCCESSFUL" or "COMPLETED" or "PAID" => PaymentStatus.Completed,
        "INITIATED" or "PENDING" or "PROCESSING" => PaymentStatus.Pending,
        "FAILED" or "DECLINED" or "EXPIRED" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // ===== Orange JSON shapes (internal) =====

    private sealed class OrangeOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class OrangeWebPaymentResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("pay_token")] public string? PayToken { get; set; }
        [JsonPropertyName("payment_url")] public string? PaymentUrl { get; set; }
        [JsonPropertyName("notif_token")] public string? NotifToken { get; set; }
    }

    private sealed class OrangeB2cResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("txnid")] public string? TxnId { get; set; }
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
    }

    private sealed class OrangeNotifPayload
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("pay_token")] public string? PayToken { get; set; }
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("notif_token")] public string? NotifToken { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("txnid")] public string? TxnId { get; set; }
        [JsonPropertyName("notification_type")] public string? NotificationType { get; set; }
    }
}
