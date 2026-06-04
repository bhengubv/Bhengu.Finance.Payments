// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
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
using Bhengu.Finance.Payments.IPay.Configuration;
using Bhengu.Finance.Payments.IPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.IPay.Providers;

/// <summary>
/// iPay (Kenya / Africa) payment gateway provider. Wraps iPay v3.
/// ProcessPaymentAsync constructs a hosted-payment-page redirect URL: the merchant builds
/// the URL with HMAC-SHA256 hex hash of concatenated fields and the customer is redirected.
/// The returned PaymentResponse carries the redirect URL in <see cref="PaymentResponse.RedirectUrl"/>
/// and the merchant order id (oid) in <see cref="PaymentResponse.GatewayReference"/>.
/// iPay v3 has no refund API — refunds throw a configuration-style exception.
/// </summary>
public sealed class IPayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly IPayOptions _options;
    private readonly IBhenguDistributedCache? _idempotencyCache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.IPay;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the iPay payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public IPayPaymentProvider(
        HttpClient httpClient,
        IOptions<IPayOptions> options,
        ILogger<IPayPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = idempotencyCache;

        if (string.IsNullOrWhiteSpace(_options.VendorId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(IPayOptions.VendorId)} is required");
        if (string.IsNullOrWhiteSpace(_options.HashKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(IPayOptions.HashKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://payments.ipayafrica.com/v3/ke"
                : _options.BaseUrl ?? "https://payments.ipayafrica.com/v3/ke";
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

            var oid = request.PaymentMethodToken;
            var inv = request.Metadata?.GetValueOrDefault("inv") ?? oid;
            var ttl = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            var tel = request.Metadata?.GetValueOrDefault("tel") ?? "";
            var eml = request.Metadata?.GetValueOrDefault("eml") ?? "";
            var vid = _options.VendorId;
            var curr = request.Currency.ToUpperInvariant();
            var p1 = request.Metadata?.GetValueOrDefault("p1") ?? "";
            var p2 = request.Metadata?.GetValueOrDefault("p2") ?? "";
            var p3 = request.Metadata?.GetValueOrDefault("p3") ?? "";
            var p4 = request.Metadata?.GetValueOrDefault("p4") ?? "";
            var cbk = _options.CallbackUrl;
            var cst = request.Metadata?.GetValueOrDefault("cst") ?? "1";
            var crl = request.Metadata?.GetValueOrDefault("crl") ?? "0";

            // iPay hash order: live + oid + inv + ttl + tel + eml + vid + curr + p1 + p2 + p3 + p4 + cbk + cst + crl
            var dataToHash = string.Concat(_options.Live, oid, inv, ttl, tel, eml, vid, curr, p1, p2, p3, p4, cbk, cst, crl);
            var hash = IPayCrypto.ComputeHmacHex(dataToHash, _options.HashKey);

            var pairs = new (string Key, string Value)[]
            {
                ("live", _options.Live), ("oid", oid), ("inv", inv), ("ttl", ttl),
                ("tel", tel), ("eml", eml), ("vid", vid), ("curr", curr),
                ("p1", p1), ("p2", p2), ("p3", p3), ("p4", p4),
                ("cbk", cbk), ("cst", cst), ("crl", crl), ("hash", hash)
            };
            var qs = string.Join('&', pairs.Select(p =>
                $"{WebUtility.UrlEncode(p.Key)}={WebUtility.UrlEncode(p.Value)}"));
            var redirectUrl = new Uri(_httpClient.BaseAddress!, "?" + qs).ToString();

            Logger.LogInformation("iPay redirect built for oid={Oid} amount={Amount}", oid, ttl);

            var response = new PaymentResponse
            {
                GatewayReference = oid,
                Status = PaymentStatus.Pending,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = redirectUrl
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", response, ct).ConfigureAwait(false);
            return response;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "iPay v3 does not expose a refund API — issue a manual refund via the iPay merchant portal.",
            providerErrorCode: "not_supported");
    }

    /// <summary>
    /// Pay funds out via iPay's M-Pesa B2C wrapper (<c>/payments/v3/mpesab2c</c>). The
    /// <see cref="PayoutRequest.DestinationToken"/> must be a Kenyan M-Pesa MSISDN
    /// (with or without leading <c>+</c>, e.g. <c>254700000000</c>).
    /// </summary>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var phone = request.DestinationToken.TrimStart('+');
            var ttl = request.Amount.ToString("0", CultureInfo.InvariantCulture);
            var occasion = request.Description;
            var reference = request.IdempotencyKey ?? $"po-{Guid.NewGuid():N}";
            // Hash order: phone + vid + ttl + reference
            var dataToHash = string.Concat(phone, _options.VendorId, ttl, reference);
            var hash = IPayCrypto.ComputeHmacHex(dataToHash, _options.HashKey);

            var body = new
            {
                phone,
                vid = _options.VendorId,
                amount = ttl,
                reference,
                occasion,
                hash
            };

            var responseBody = await IPayHttpClient.SendJsonAsync(_httpClient, Logger, HttpMethod.Post, "payments/v3/mpesab2c", body, ct, "ProcessPayout").ConfigureAwait(false);
            var payout = JsonSerializer.Deserialize<IPayPayoutResponse>(responseBody);

            Logger.LogInformation("iPay M-Pesa B2C payout: txn={Txn} status={Status}",
                payout?.TransactionId, payout?.Status);

            var status = payout?.Status?.ToLowerInvariant() switch
            {
                "success" or "successful" or "completed" => PaymentStatus.Completed,
                "failed" or "declined" => PaymentStatus.Failed,
                _ => PaymentStatus.Pending
            };

            var response = new PayoutResponse
            {
                GatewayReference = payout?.TransactionId ?? reference,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", response, ct).ConfigureAwait(false);
            return response;
        }, ct);
    }

    /// <summary>
    /// Initiate a direct M-Pesa C2B charge via iPay's mobile SDK endpoint (api/sdk/v3/mpesa).
    /// Returns the iPay transaction status payload.
    /// </summary>
    public async Task<string> ChargeMpesaAsync(string phone, decimal amount, string oid, CancellationToken ct = default)
    {
        var ttl = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var dataToHash = string.Concat(phone, _options.VendorId, ttl, oid);
        var hash = IPayCrypto.ComputeHmacHex(dataToHash, _options.HashKey);
        var body = new { phone, vid = _options.VendorId, amount = ttl, oid, hash };
        return await IPayHttpClient.SendJsonAsync(_httpClient, Logger, HttpMethod.Post, "api/sdk/v3/mpesa", body, ct, "ChargeMpesa").ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.HashKey))
            {
                Logger.LogWarning("iPay HashKey not configured — webhook verification cannot proceed.");
                return false;
            }

            try
            {
                // iPay's callback re-uses the same HMAC-SHA256-hex scheme over the concatenated
                // payload string. Caller must concatenate fields in the documented order before
                // invoking VerifyWebhookSignature.
                var expected = IPayCrypto.ComputeHmacHex(payload, _options.HashKey);
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature),
                    Encoding.UTF8.GetBytes(expected));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "iPay webhook signature verification raised");
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
                // iPay callback can be application/x-www-form-urlencoded (txncd=..&status=..&...)
                // or JSON. Try JSON first; fall back to form-urlencoded.
                string? statusRaw, txncd, ipnid, eventType, amountRaw, currencyRaw;
                if (payload.TrimStart().StartsWith('{'))
                {
                    var json = JsonSerializer.Deserialize<IPayCallback>(payload);
                    if (json is null) return Task.FromResult<WebhookEvent?>(null);
                    statusRaw = json.Status;
                    txncd = json.Txncd;
                    ipnid = json.Ipnid;
                    eventType = json.EventType;
                    amountRaw = json.Amount;
                    currencyRaw = json.Currency;
                }
                else
                {
                    var bag = ParseQueryString(payload);
                    statusRaw = bag.GetValueOrDefault("status");
                    txncd = bag.GetValueOrDefault("txncd");
                    ipnid = bag.GetValueOrDefault("ipnid");
                    eventType = bag.GetValueOrDefault("event");
                    amountRaw = bag.GetValueOrDefault("amount");
                    currencyRaw = bag.GetValueOrDefault("currency");
                }

                var reference = txncd ?? ipnid;
                if (string.IsNullOrEmpty(reference))
                    return Task.FromResult<WebhookEvent?>(null);

                decimal.TryParse(amountRaw, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount);
                var currency = currencyRaw ?? _options.Currency;
                var eventLower = eventType?.ToLowerInvariant();
                var statusLower = statusRaw?.ToLowerInvariant();

                WebhookEvent? typed = (eventLower, statusLower) switch
                {
                    ("payout.completed", _) => new PayoutCompletedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = eventType,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency
                    },
                    ("payout.failed", _) => new PayoutFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = eventType,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency
                    },
                    (_, "aei7p7yrx4ae34") => new ChargeSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = statusRaw,
                        Category = WebhookEventCategory.ChargeSucceeded,
                        Amount = amount,
                        Currency = currency
                    },
                    (_, "bdi6p2yy76etrs") or (_, "fe2707etr5s4wq") or (_, "dtfi4p7yty45wq") => new ChargeFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = statusRaw,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = statusRaw
                    },
                    (_, "cr5i3pgy9867e1") => new ChargePendingEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Pending,
                        EventType = statusRaw,
                        Category = WebhookEventCategory.ChargePending,
                        Amount = amount,
                        Currency = currency
                    },
                    _ => null
                };

                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse iPay callback");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"ipay:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"ipay:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ParseQueryString(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = WebUtility.UrlDecode(pair[..eq]);
            var val = WebUtility.UrlDecode(pair[(eq + 1)..]);
            dict[key] = val;
        }
        return dict;
    }

    private sealed class IPayCallback
    {
        [JsonPropertyName("txncd")] public string? Txncd { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("ipnid")] public string? Ipnid { get; set; }
        [JsonPropertyName("mc")] public string? Mc { get; set; }
        [JsonPropertyName("event")] public string? EventType { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class IPayPayoutResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
