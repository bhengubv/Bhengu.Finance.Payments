// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayFast.Providers;

/// <summary>
/// PayFast implementation of <see cref="IMandateProvider"/> backed by PayFast's
/// <em>tokenisation</em> flow (<c>subscription_type=2</c>, ad-hoc agreements).
/// </summary>
/// <remarks>
/// <para>The flow is two-stage. <see cref="CreateMandateAsync"/> returns a <see cref="Mandate"/>
/// with an <see cref="Mandate.AuthorisationUrl"/> the consumer redirects the payer to. PayFast
/// charges R0 to set up the token, then issues the <c>token</c> field via IPN webhook — the
/// merchant persists it as the mandate reference.</para>
/// <para><see cref="ChargeMandateAsync"/> POSTs to <c>subscriptions/{token}/adhoc</c> with the
/// amount, item name, and signature. PayFast pulls the funds from the same card / EFT account that
/// authorised the original tokenisation.</para>
/// <para>PayFast does not expose a separate <em>mandate amount limit</em> field — the per-debit
/// ceiling is enforced by the merchant. The SDK records the request-supplied <c>AmountLimit</c>
/// in the local <see cref="PayFastMandateAmountCache"/> so subsequent
/// <see cref="ChargeMandateAsync"/> calls can refuse over-limit pulls before they hit the network.</para>
/// </remarks>
public sealed class PayFastMandateProvider : IMandateProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayFastOptions _options;
    private readonly ILogger<PayFastMandateProvider> _logger;
    private readonly PayFastMandateAmountCache _amountLimits;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.PayFast;

    /// <summary>Construct a PayFast mandate provider. Designed to be registered via DI.</summary>
    public PayFastMandateProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        ILogger<PayFastMandateProvider> logger,
        PayFastMandateAmountCache amountLimits)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _amountLimits = amountLimits ?? throw new ArgumentNullException(nameof(amountLimits));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayFastOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? "https://sandbox.payfast.co.za/"
                : "https://api.payfast.co.za/");
        }
    }

    /// <inheritdoc/>
    public Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mPaymentId = request.IdempotencyKey ?? $"mand-{Guid.NewGuid():N}";

        var formData = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant_id"] = _options.MerchantId,
            ["merchant_key"] = _options.MerchantKey,
            ["return_url"] = _options.ReturnUrl ?? string.Empty,
            ["cancel_url"] = _options.CancelUrl ?? string.Empty,
            ["notify_url"] = _options.NotifyUrl ?? string.Empty,
            ["m_payment_id"] = mPaymentId,
            ["amount"] = "0",
            ["item_name"] = "Card Tokenisation",
            ["item_description"] = request.Description,
            ["currency"] = request.Currency,
            ["custom_str1"] = request.CustomerId,
            ["custom_str2"] = mPaymentId,
            ["subscription_type"] = "2"
        };

        var signature = PayFastSignatureHelper.ComputeRedirectSignature(formData, _options.Passphrase ?? string.Empty);

        var qsParts = formData
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={WebUtility.UrlEncode(kv.Value.Trim())}")
            .ToList();
        qsParts.Add($"signature={signature}");
        var authorisationUrl = $"{GetRedirectBaseUrl()}/eng/process?{string.Join("&", qsParts)}";

        _amountLimits.Set(mPaymentId, request.AmountLimit, request.Currency);

        var mandate = new Mandate
        {
            Reference = mPaymentId,
            CustomerId = request.CustomerId,
            Status = MandateStatus.Pending,
            AmountLimit = request.AmountLimit,
            Currency = request.Currency,
            AuthorisationUrl = authorisationUrl
        };

        _logger.LogInformation("PayFast mandate redirect prepared: m_payment_id={MPaymentId} customer={CustomerId} limit={AmountLimit} {Currency}",
            mPaymentId, request.CustomerId, request.AmountLimit, request.Currency);

        return Task.FromResult(mandate);
    }

    /// <inheritdoc/>
    public async Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);

        var fetched = await SendSignedAsync<PayFastTokenFetchResponse>(
            HttpMethod.Get, $"subscriptions/{Uri.EscapeDataString(mandateReference)}/fetch", new Dictionary<string, string>(), ct)
            .ConfigureAwait(false);

        if (fetched?.Data is null)
            return null;

        var (cachedLimit, cachedCurrency) = _amountLimits.TryGet(mandateReference);

        return new Mandate
        {
            Reference = mandateReference,
            CustomerId = fetched.Data.CustomStr1 ?? string.Empty,
            Status = MapMandateStatus(fetched.Data.Status),
            AmountLimit = cachedLimit ?? 0m,
            Currency = cachedCurrency ?? "ZAR",
            AuthorisedAt = fetched.Data.RunDate,
            CancelledAt = MapMandateStatus(fetched.Data.Status) == MandateStatus.Cancelled ? DateTime.UtcNow : null
        };
    }

    /// <inheritdoc/>
    public async Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);

        try
        {
            await SendSignedAsync<object>(
                HttpMethod.Put, $"subscriptions/{Uri.EscapeDataString(mandateReference)}/cancel", new Dictionary<string, string>(), ct)
                .ConfigureAwait(false);
            _logger.LogInformation("PayFast mandate cancelled: {Reference}", mandateReference);
        }
        catch (PaymentDeclinedException ex) when (
            ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true ||
            ex.ProviderErrorMessage?.Contains("cancel", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Idempotent: already cancelled is success.
            _logger.LogInformation("PayFast mandate already cancelled (idempotent): {Reference}", mandateReference);
        }

        var (limit, currency) = _amountLimits.TryGet(mandateReference);
        return new Mandate
        {
            Reference = mandateReference,
            CustomerId = string.Empty,
            Status = MandateStatus.Cancelled,
            AmountLimit = limit ?? 0m,
            Currency = currency ?? "ZAR",
            CancelledAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public async Task<PaymentResponse> ChargeMandateAsync(MandateChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (limit, _) = _amountLimits.TryGet(request.MandateReference);
        if (limit is not null && request.Amount > limit.Value)
        {
            throw new BhenguPaymentException(
                ProviderName,
                $"PayFast mandate {request.MandateReference} amount {request.Amount} exceeds limit {limit.Value}",
                "amount_over_limit");
        }

        var amountInCents = (long)(request.Amount * 100m);
        var bodyParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["amount"] = amountInCents.ToString(CultureInfo.InvariantCulture),
            ["item_name"] = request.Description
        };

        if (request.IdempotencyKey is { Length: > 0 } idem)
            bodyParams["m_payment_id"] = idem;

        var path = $"subscriptions/{Uri.EscapeDataString(request.MandateReference)}/adhoc";

        var result = await SendSignedAsync<PayFastAdhocResponse>(HttpMethod.Post, path, bodyParams, ct).ConfigureAwait(false);
        var status = MapPaymentStatus(result?.Data?.Response);

        _logger.LogInformation("PayFast mandate charged: {Mandate} amount={Amount} response={Response}",
            request.MandateReference, request.Amount, result?.Data?.Response);

        return new PaymentResponse
        {
            GatewayReference = result?.Data?.PfPaymentId ?? string.Empty,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = result?.Data?.ResponseReason
        };
    }

    private async Task<T?> SendSignedAsync<T>(
        HttpMethod method,
        string relativePath,
        IDictionary<string, string> bodyParams,
        CancellationToken ct) where T : class
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        var signature = PayFastSignatureHelper.ComputeApiSignature(
            _options.MerchantId,
            _options.Passphrase ?? string.Empty,
            timestamp,
            bodyParams);

        var url = relativePath + (_options.UseSandbox
            ? (relativePath.Contains('?', StringComparison.Ordinal) ? "&testing=true" : "?testing=true")
            : string.Empty);

        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("merchant-id", _options.MerchantId);
        req.Headers.Add("version", "v1");
        req.Headers.Add("timestamp", timestamp);
        req.Headers.Add("signature", signature);

        if (bodyParams.Count > 0 && method != HttpMethod.Get)
            req.Content = new FormUrlEncodedContent(bodyParams);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "PayFast API HTTP failure", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayFast {Method} {Path} failed: {Status} {Body}", method, relativePath, response.StatusCode, body);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "PayFast {Method} {Path} returned non-JSON body — accepting as ack", method, relativePath);
            return null;
        }
    }

    private string GetRedirectBaseUrl() => _options.UseSandbox
        ? (_options.SandboxUrl ?? "https://sandbox.payfast.co.za")
        : (_options.BaseUrl ?? "https://www.payfast.co.za");

    private static MandateStatus MapMandateStatus(int? status) => status switch
    {
        1 => MandateStatus.Active,
        2 => MandateStatus.Cancelled,
        3 => MandateStatus.Paused,
        4 => MandateStatus.Expired,
        _ => MandateStatus.Pending
    };

    private static PaymentStatus MapPaymentStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "APPROVED" or "COMPLETE" or "COMPLETED" => PaymentStatus.Completed,
        "PENDING" => PaymentStatus.Pending,
        "FAILED" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // === PayFast API shapes (internal) ===

    private sealed class PayFastAdhocResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public PayFastAdhocData? Data { get; set; }
    }

    private sealed class PayFastAdhocData
    {
        [JsonPropertyName("message")] public bool Message { get; set; }
        [JsonPropertyName("pf_payment_id")] public string? PfPaymentId { get; set; }
        [JsonPropertyName("response")] public string? Response { get; set; }
        [JsonPropertyName("response_reason")] public string? ResponseReason { get; set; }
    }

    private sealed class PayFastTokenFetchResponse
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public PayFastTokenData? Data { get; set; }
    }

    private sealed class PayFastTokenData
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("status")] public int? Status { get; set; }
        [JsonPropertyName("status_text")] public string? StatusText { get; set; }
        [JsonPropertyName("run_date")] public DateTime? RunDate { get; set; }
        [JsonPropertyName("custom_str1")] public string? CustomStr1 { get; set; }
    }
}

/// <summary>
/// Distributed-cache-backed registry of per-mandate amount limits and currencies. PayFast
/// doesn't expose a mandate-side amount cap, so the SDK enforces it client-side using the limit
/// captured at <see cref="PayFastMandateProvider.CreateMandateAsync"/> time.
/// </summary>
/// <remarks>
/// Entries are written to <see cref="Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache"/>
/// with a 365-day TTL so mandate limits survive restarts and remain consistent across replicas
/// when Redis is wired up via the optional <c>Bhengu.Finance.Payments.Redis</c> package.
/// </remarks>
public sealed class PayFastMandateAmountCache
{
    private const string KeyPrefix = "payfast:mandate-amount:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(365);

    private readonly Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache _cache;

    /// <summary>Construct with an injected distributed cache. Used in DI-driven scenarios.</summary>
    public PayFastMandateAmountCache(Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests and back-compat callers.</summary>
    public PayFastMandateAmountCache() : this(new Bhengu.Finance.Payments.Core.Caching.InMemoryBhenguDistributedCache()) { }

    /// <summary>Store the limit / currency for a mandate.</summary>
    public void Set(string mandateReference, decimal amountLimit, string currency)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        ArgumentException.ThrowIfNullOrEmpty(currency);
        _cache.SetAsync(KeyPrefix + mandateReference, new MandateLimitEntry(amountLimit, currency), TimeToLive).GetAwaiter().GetResult();
    }

    /// <summary>Retrieve the cached limit / currency for a mandate, or (null, null) if not present.</summary>
    public (decimal? Limit, string? Currency) TryGet(string mandateReference)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        var entry = _cache.GetAsync<MandateLimitEntry>(KeyPrefix + mandateReference).GetAwaiter().GetResult();
        return entry is null ? (null, null) : (entry.Limit, entry.Currency);
    }

    /// <summary>Remove a mandate's cached limit. Returns true if a value was previously cached.</summary>
    public bool Remove(string mandateReference)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        var existed = _cache.GetAsync<MandateLimitEntry>(KeyPrefix + mandateReference).GetAwaiter().GetResult() is not null;
        _cache.RemoveAsync(KeyPrefix + mandateReference).GetAwaiter().GetResult();
        return existed;
    }

    /// <summary>Serialisable record persisted in the distributed cache.</summary>
    public sealed record MandateLimitEntry(decimal Limit, string Currency);
}
