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
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ExpressPay.Providers;

/// <summary>
/// ExpressPay (Ghana, Gambia, Sierra Leone, Liberia, Nigeria) payment gateway provider.
/// Wraps the form-encoded submit.php / query.php / payout.php API. ExpressPay does NOT issue HMAC on
/// the post-url callback; <see cref="VerifyWebhookSignature"/> performs a constant-time
/// equality check between the supplied signature and the configured ApiKey, requiring
/// the caller to forward the api-key in a trusted reverse-proxy header.
/// </summary>
public sealed class ExpressPayPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly ExpressPayOptions _options;
    private readonly ILogger<ExpressPayPaymentProvider> _logger;
    private readonly IBhenguDistributedCache? _idempotencyCache;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.ExpressPay;

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

    /// <summary>Construct the ExpressPay payment provider. <paramref name="idempotencyCache"/> is optional — when omitted, idempotency replay is a no-op.</summary>
    public ExpressPayPaymentProvider(
        HttpClient httpClient,
        IOptions<ExpressPayOptions> options,
        ILogger<ExpressPayPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotencyCache = idempotencyCache;

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://sandbox.expresspaygh.com/api/"
                : _options.BaseUrl ?? "https://expresspay.com.gh/api/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
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
            var form = new Dictionary<string, string>
            {
                ["merchant-id"] = _options.MerchantId,
                ["api-key"] = _options.ApiKey,
                ["currency"] = request.Currency.ToUpperInvariant(),
                ["amount"] = request.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["order-id"] = request.PaymentMethodToken,
                ["order-desc"] = request.Description,
                ["redirect-url"] = _options.RedirectUrl,
                ["post-url"] = _options.PostUrl,
                ["accountnumber"] = request.Metadata?.GetValueOrDefault("accountnumber") ?? "",
                ["username"] = request.Metadata?.GetValueOrDefault("username") ?? "",
                ["email"] = request.Metadata?.GetValueOrDefault("email") ?? "",
                ["firstname"] = request.Metadata?.GetValueOrDefault("firstname") ?? "",
                ["lastname"] = request.Metadata?.GetValueOrDefault("lastname") ?? ""
            };

            var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, _logger, HttpMethod.Post, "submit.php", form, ct, "ProcessPayment").ConfigureAwait(false);
            var submit = JsonSerializer.Deserialize<ExpressPaySubmitResponse>(responseBody);

            _logger.LogInformation("ExpressPay submit: status={Status} token={Token} url={Url}",
                submit?.Status, submit?.Token, submit?.PaymentUrl);

            var response = new PaymentResponse
            {
                GatewayReference = submit?.Token ?? request.PaymentMethodToken,
                Status = submit?.Status == 1 ? PaymentStatus.Pending : PaymentStatus.Failed,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = submit?.PaymentUrl,
                Message = submit?.Message
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", response, ct).ConfigureAwait(false);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", response.Status == PaymentStatus.Pending
                    ? BhenguPaymentDiagnostics.Outcomes.Pending
                    : BhenguPaymentDiagnostics.Outcomes.Declined));
            activity.SetOutcome(response.Status == PaymentStatus.Pending
                ? BhenguPaymentDiagnostics.Outcomes.Pending
                : BhenguPaymentDiagnostics.Outcomes.Declined);
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

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "ExpressPay does not expose a refund API — issue refunds via the ExpressPay merchant portal.",
            providerErrorCode: "not_supported");
    }

    /// <summary>
    /// Pay funds out to an ExpressPay-supported beneficiary (mobile money or bank). The
    /// <see cref="PayoutRequest.DestinationToken"/> must take the form <c>"<account_type>:<account_number>"</c>
    /// where <c>account_type</c> is one of <c>mtn|airteltigo|vodafone|bank</c> and <c>account_number</c> is the
    /// destination msisdn or NUBAN.
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
            var colon = request.DestinationToken.IndexOf(':');
            if (colon <= 0)
                throw new BhenguPaymentException(ProviderName,
                    "ExpressPay PayoutRequest.DestinationToken must be '<account_type>:<account_number>' (account_type one of mtn|airteltigo|vodafone|bank)");
            var accountType = request.DestinationToken[..colon];
            var accountNumber = request.DestinationToken[(colon + 1)..];
            var batchRef = request.IdempotencyKey ?? $"po-{Guid.NewGuid():N}";

            var form = new Dictionary<string, string>
            {
                ["merchant-id"] = _options.MerchantId,
                ["api-key"] = _options.ApiKey,
                ["account-type"] = accountType,
                ["account-number"] = accountNumber,
                ["amount"] = request.Amount.ToString("0.00", CultureInfo.InvariantCulture),
                ["currency"] = request.Currency.ToUpperInvariant(),
                ["description"] = request.Description,
                ["batch-reference"] = batchRef
            };

            var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, _logger, HttpMethod.Post, "payout.php", form, ct, "ProcessPayout").ConfigureAwait(false);
            var payout = JsonSerializer.Deserialize<ExpressPayPayoutResponse>(responseBody);

            _logger.LogInformation("ExpressPay payout: status={Status} txn={Txn} ref={Ref}",
                payout?.Status, payout?.TransactionId, batchRef);

            var status = payout?.Status switch
            {
                1 => PaymentStatus.Pending,
                2 => PaymentStatus.Completed,
                3 => PaymentStatus.Failed,
                _ => PaymentStatus.Pending
            };

            var response = new PayoutResponse
            {
                GatewayReference = payout?.TransactionId ?? batchRef,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", response, ct).ConfigureAwait(false);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", status == PaymentStatus.Failed
                    ? BhenguPaymentDiagnostics.Outcomes.Declined
                    : BhenguPaymentDiagnostics.Outcomes.Success));
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
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
    /// Query the status of an ExpressPay token via query.php. Returns the raw API response body.
    /// </summary>
    public async Task<string> QueryStatusAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var form = new Dictionary<string, string>
        {
            ["merchant-id"] = _options.MerchantId,
            ["api-key"] = _options.ApiKey,
            ["token"] = token
        };
        return await ExpressPayHttpClient.SendFormAsync(_httpClient, _logger, HttpMethod.Post, "query.php", form, ct, "QueryStatus").ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("ExpressPay ApiKey not configured — webhook verification cannot succeed.");
            BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("valid", false));
            return false;
        }

        // ExpressPay does NOT HMAC its post-url callbacks. Constant-time compare the supplied
        // signature (which the caller must source from a trusted reverse-proxy header) with the
        // configured ApiKey. Production callers SHOULD additionally call QueryStatusAsync(token).
        var a = Encoding.UTF8.GetBytes(signature);
        var b = Encoding.UTF8.GetBytes(_options.ApiKey);
        var valid = a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
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
            ExpressPayCallback? cb;
            if (payload.TrimStart().StartsWith('{'))
            {
                cb = JsonSerializer.Deserialize<ExpressPayCallback>(payload);
            }
            else
            {
                var bag = ParseForm(payload);
                int.TryParse(bag.GetValueOrDefault("status"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sint);
                cb = new ExpressPayCallback
                {
                    Token = bag.GetValueOrDefault("token"),
                    Status = sint,
                    Currency = bag.GetValueOrDefault("currency"),
                    Amount = bag.GetValueOrDefault("amount"),
                    EventType = bag.GetValueOrDefault("event")
                };
            }

            if (cb is null || string.IsNullOrEmpty(cb.Token))
                return Task.FromResult<WebhookEvent?>(null);

            decimal.TryParse(cb.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount);
            var currency = cb.Currency ?? _options.Currency;
            var eventLower = cb.EventType?.ToLowerInvariant();

            WebhookEvent? typed = (eventLower, cb.Status) switch
            {
                ("payout.completed", _) => new PayoutCompletedEvent
                {
                    GatewayReference = cb.Token!,
                    Status = PaymentStatus.Completed,
                    EventType = cb.EventType,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = cb.Token!,
                    Amount = amount,
                    Currency = currency
                },
                ("payout.failed", _) => new PayoutFailedEvent
                {
                    GatewayReference = cb.Token!,
                    Status = PaymentStatus.Failed,
                    EventType = cb.EventType,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = cb.Token!,
                    Amount = amount,
                    Currency = currency
                },
                (_, 1) => new ChargeSucceededEvent
                {
                    GatewayReference = cb.Token!,
                    Status = PaymentStatus.Completed,
                    EventType = cb.EventType ?? "1",
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency
                },
                (_, 2) => new ChargePendingEvent
                {
                    GatewayReference = cb.Token!,
                    Status = PaymentStatus.Pending,
                    EventType = cb.EventType ?? "2",
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                },
                (_, 3) => new ChargeFailedEvent
                {
                    GatewayReference = cb.Token!,
                    Status = PaymentStatus.Failed,
                    EventType = cb.EventType ?? "3",
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency
                },
                (_, 4) => new WebhookEvent
                {
                    GatewayReference = cb.Token!,
                    Status = PaymentStatus.Cancelled,
                    EventType = cb.EventType ?? "4",
                    Category = WebhookEventCategory.Unknown
                },
                _ => null
            };

            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ExpressPay callback");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return null;
        var key = $"expresspay:{operation}:{idempotencyKey}";
        return await _idempotencyCache.GetAsync<T>(key, ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (_idempotencyCache is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return;
        var key = $"expresspay:{operation}:{idempotencyKey}";
        await _idempotencyCache.SetAsync(key, value, TimeSpan.FromHours(24), ct).ConfigureAwait(false);
    }

    private static string ClassifyOutcome(Exception ex) => ex switch
    {
        PaymentDeclinedException => BhenguPaymentDiagnostics.Outcomes.Declined,
        ProviderRateLimitException => BhenguPaymentDiagnostics.Outcomes.RateLimited,
        ProviderUnavailableException => BhenguPaymentDiagnostics.Outcomes.Unavailable,
        _ => BhenguPaymentDiagnostics.Outcomes.Error
    };

    private static Dictionary<string, string> ParseForm(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            dict[WebUtility.UrlDecode(pair[..eq])] = WebUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return dict;
    }

    private sealed class ExpressPaySubmitResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("payment_url")] public string? PaymentUrl { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class ExpressPayPayoutResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("transaction-id")] public string? TransactionId { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class ExpressPayCallback
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("event")] public string? EventType { get; set; }
    }
}
