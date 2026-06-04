// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco implementation of <see cref="ITokenisationProvider"/>.
/// </summary>
/// <remarks>
/// <para>Yoco's tokenisation API works via the <c>/v1/checkouts</c> endpoint. The merchant POSTs
/// the intended amount and currency; Yoco returns a hosted checkout URL where the payer enters
/// card details and the resulting card token surfaces back via the webhook (<c>payment.succeeded</c>
/// or <c>checkout.succeeded</c>).</para>
/// <para>Yoco does NOT expose a public read-by-token endpoint, so
/// <see cref="GetPaymentMethodAsync"/> consults an in-memory cache of methods captured at
/// tokenisation time and returns <c>null</c> when the token isn't known to this process.
/// Production callers should persist their own (token → method) index when the webhook arrives.</para>
/// <para>Yoco does NOT list tokens per customer either, so <see cref="ListPaymentMethodsAsync"/>
/// returns an empty list. <see cref="DeletePaymentMethodAsync"/> calls Yoco's <c>/v1/cards/{cardId}</c>
/// DELETE if available; otherwise the SDK reports success and the merchant is responsible for
/// dropping the token from their own vault.</para>
/// </remarks>
public sealed class YocoTokenisationProvider : ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;
    private readonly ILogger<YocoTokenisationProvider> _logger;
    private readonly YocoTokenCache _cache;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Yoco;

    /// <summary>Construct a Yoco tokenisation provider. Designed to be registered via DI.</summary>
    public YocoTokenisationProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoTokenisationProvider> logger,
        YocoTokenCache cache)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Yoco's hosted-checkout API expects an amount-in-cents tokenisation pre-auth.
        // We use a R0.01 minimum so the checkout actually opens; the merchant later voids
        // or refunds it before settlement. For test purposes the auth amount can be any
        // positive integer. Callers can override via TokeniseRequest.DisplayName.
        var requestBody = new
        {
            amount = 100, // 1 ZAR in cents — minimum to open the Yoco hosted page
            currency = "ZAR",
            metadata = new
            {
                tokenisation_only = true,
                display_name = request.DisplayName,
                customer_id = request.CustomerId,
                set_as_default = request.SetAsDefault
            }
        };

        var body = await SendAsync(HttpMethod.Post, "checkouts", requestBody, ct, "Tokenise").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<YocoCheckoutResponse>(body);

        if (response is null || string.IsNullOrEmpty(response.Id))
            throw new BhenguPaymentException(ProviderName, "Yoco checkout returned no id", "no_checkout_id");

        var method = new PaymentMethod
        {
            Token = response.Id,
            CustomerId = request.CustomerId,
            Kind = PaymentMethodKind.Card,
            Brand = null,
            Last4 = null,
            ExpiryMonth = null,
            ExpiryYear = null,
            DisplayName = request.DisplayName,
            IsDefault = request.SetAsDefault,
            CreatedAt = DateTime.UtcNow
        };

        _cache.Set(method);
        _logger.LogInformation(
            "Yoco checkout opened for tokenisation: id={CheckoutId} redirect={Redirect}",
            response.Id, response.RedirectUrl);

        return method;
    }

    /// <inheritdoc/>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        var cached = _cache.TryGet(token);
        if (cached is null)
        {
            _logger.LogInformation(
                "Yoco has no GET-by-token endpoint; returning null for {Token}. Persist tokens on the webhook for cross-process lookups.",
                token);
        }
        return Task.FromResult(cached);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);

        _logger.LogInformation(
            "Yoco does not list tokens per customer; returning empty list for {CustomerId}. Persist a (customer → token) index in your own store.",
            customerId);

        return Task.FromResult<IReadOnlyList<PaymentMethod>>(Array.Empty<PaymentMethod>());
    }

    /// <inheritdoc/>
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        // Yoco's documented merchant API does not expose a card-delete endpoint. We attempt the
        // optional `/cards/{token}` DELETE for tenants that have the beta endpoint enabled;
        // otherwise we evict the in-process cache and return true so callers can move on.
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"cards/{Uri.EscapeDataString(token)}");
            HttpResponseMessage resp;
            try
            {
                resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogInformation(ex, "Yoco DELETE /cards is unavailable; treating as client-side delete for {Token}", token);
                _cache.Remove(token);
                return true;
            }

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                _cache.Remove(token);
                return false;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Yoco DELETE /cards/{Token} returned {Status} — treating as client-side delete. Body={Body}",
                    token, resp.StatusCode, body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Yoco DELETE /cards/{Token} raised; treating as client-side delete", token);
        }

        _cache.Remove(token);
        return true;
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Yoco failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    // === Yoco API shapes (internal) ===

    private sealed class YocoCheckoutResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("redirectUrl")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public int Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}

/// <summary>
/// Distributed-cache-backed registry of Yoco card tokens captured at tokenisation / webhook time.
/// Yoco has no public GET-by-token endpoint, so the SDK records what it knows here and the
/// caller is responsible for refreshing the cache on each webhook delivery.
/// </summary>
/// <remarks>
/// Entries are written to <see cref="Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache"/>
/// with a 365-day TTL so tokens survive process restarts and remain consistent across replicas
/// when Redis is wired up via the optional <c>Bhengu.Finance.Payments.Redis</c> package.
/// </remarks>
public sealed class YocoTokenCache
{
    private const string KeyPrefix = "yoco:token:";
    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(365);

    private readonly Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache _cache;

    /// <summary>Construct with an injected distributed cache. Used in DI-driven scenarios.</summary>
    public YocoTokenCache(Bhengu.Finance.Payments.Core.Caching.IBhenguDistributedCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Default-constructor convenience for tests and back-compat callers.</summary>
    public YocoTokenCache() : this(new Bhengu.Finance.Payments.Core.Caching.InMemoryBhenguDistributedCache()) { }

    /// <summary>Add or overwrite a payment-method entry keyed by its token.</summary>
    public void Set(PaymentMethod method)
    {
        ArgumentNullException.ThrowIfNull(method);
        _cache.SetAsync(KeyPrefix + method.Token, method, TimeToLive).GetAwaiter().GetResult();
    }

    /// <summary>Retrieve a cached payment method, or <c>null</c> if not present.</summary>
    public PaymentMethod? TryGet(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return _cache.GetAsync<PaymentMethod>(KeyPrefix + token).GetAwaiter().GetResult();
    }

    /// <summary>Remove a payment-method entry. Returns true if the token existed previously.</summary>
    public bool Remove(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var existed = _cache.GetAsync<PaymentMethod>(KeyPrefix + token).GetAwaiter().GetResult() is not null;
        _cache.RemoveAsync(KeyPrefix + token).GetAwaiter().GetResult();
        return existed;
    }
}
