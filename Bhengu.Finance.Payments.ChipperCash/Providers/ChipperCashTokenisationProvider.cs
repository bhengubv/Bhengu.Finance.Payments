// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ChipperCash.Providers;

/// <summary>
/// Chipper Cash READ-side implementation of <see cref="ITokenisationProvider"/>. Chipper does not
/// vault card PANs — instead it stores <em>saved recipients</em> (named mobile-money / wallet
/// destinations keyed by MSISDN and network). The returned <see cref="PaymentMethod.Token"/> is the
/// saved recipient identifier; pass it as <see cref="Core.Models.PayoutRequest.DestinationToken"/>
/// on subsequent disbursements to skip the per-call beneficiary enrolment fields.
/// </summary>
/// <remarks>
/// For WRITE operations (enrolling new recipients), see
/// <see cref="ChipperCashRawCardTokenisationProvider"/>.
/// </remarks>
public sealed class ChipperCashTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly ChipperCashOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.ChipperCash;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public ChipperCashTokenisationProvider(
        HttpClient httpClient,
        IOptions<ChipperCashOptions> options,
        ILogger<ChipperCashTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiSecret)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.chippercash.com/");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(_options.ApiKey);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("get_payment_method", async () =>
        {
            try
            {
                var body = await ChipperCashHttp.SendAsync(_httpClient, _options, HttpMethod.Get, $"v1/recipients/{Uri.EscapeDataString(token)}", null, ct, Logger, ProviderName, "GetPaymentMethod").ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<ChipperCashRecipientResponse>(body);
                return resp is null ? null : MapRecipient(resp);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);

        var list = await RunOperationAsync("list_payment_methods", async () =>
        {
            var body = await ChipperCashHttp.SendAsync(_httpClient, _options, HttpMethod.Get, $"v1/customers/{Uri.EscapeDataString(customerId)}/recipients", null, ct, Logger, ProviderName, "ListPaymentMethods").ConfigureAwait(false);
            return JsonSerializer.Deserialize<ChipperCashRecipientListResponse>(body);
        }, ct).ConfigureAwait(false);

        if (list?.Recipients is null) yield break;
        foreach (var r in list.Recipients)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapRecipient(r);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("delete_payment_method", async () =>
        {
            try
            {
                await ChipperCashHttp.SendAsync(_httpClient, _options, HttpMethod.Delete, $"v1/recipients/{Uri.EscapeDataString(token)}", null, ct, Logger, ProviderName, "DeletePaymentMethod").ConfigureAwait(false);
                return true;
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return false;
            }
        }, ct);
    }

    internal static PaymentMethod MapRecipient(ChipperCashRecipientResponse r) => new()
    {
        Token = r.Id ?? string.Empty,
        CustomerId = r.CustomerId,
        Kind = PaymentMethodKind.MobileMoney,
        Brand = r.Network,
        Last4 = !string.IsNullOrEmpty(r.Msisdn) && r.Msisdn.Length > 4 ? r.Msisdn[^4..] : r.Msisdn,
        DisplayName = r.Name,
        IsDefault = r.IsDefault ?? false,
        CreatedAt = r.CreatedAt
    };

    internal sealed class ChipperCashRecipientResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("customerId")] public string? CustomerId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("msisdn")] public string? Msisdn { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("isDefault")] public bool? IsDefault { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }

    internal sealed class ChipperCashRecipientListResponse
    {
        [JsonPropertyName("recipients")] public List<ChipperCashRecipientResponse>? Recipients { get; set; }
    }
}

/// <summary>
/// Chipper Cash WRITE-side implementation of <see cref="IRawCardTokenisationProvider"/>. Chipper
/// does not vault card PANs — the underlying call enrols a <em>saved recipient</em>
/// (named mobile-money / wallet destination keyed by MSISDN and network). The returned
/// <see cref="PaymentMethod.Token"/> is the saved recipient identifier.
/// </summary>
/// <remarks>
/// Although this implements the Raw-Card surface for interface consistency, no card PAN is sent
/// upstream. The <see cref="TokeniseRequest.Card"/>'s <c>CardNumber</c> is interpreted as the
/// MSISDN and <c>CardholderName</c> as the network. PCI scope is therefore unchanged from the
/// read-side adapter; the type marker exists to make the WRITE intent explicit at the call site.
/// </remarks>
public sealed class ChipperCashRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly ChipperCashOptions _options;
    private readonly IBhenguDistributedCache _cache;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.ChipperCash;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public ChipperCashRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<ChipperCashOptions> options,
        ILogger<ChipperCashRawCardTokenisationProvider> logger,
        IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ChipperCashOptions.ApiSecret)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.chippercash.com/");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(_options.ApiKey);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("tokenise", async () =>
        {
            var cached = await TryGetCachedAsync<PaymentMethod>(request.IdempotencyKey, "tokenise", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            // Chipper Cash saved-recipient enrolment: the MSISDN derives from CardholderName (used as
            // a display name proxy), CardNumber (re-purposed as the MSISDN string), and the
            // <c>network</c> hint encoded into DisplayName when callers do not supply card details.
            var msisdn = request.Card.CardNumber;
            var network = request.Card.CardholderName ?? "MTN";

            var requestBody = new
            {
                customerId = request.CustomerId,
                name = request.DisplayName ?? "Saved recipient",
                country = _options.Country,
                mobile = new { msisdn, network },
                isDefault = request.SetAsDefault
            };

            var body = await ChipperCashHttp.SendAsync(_httpClient, _options, HttpMethod.Post, "v1/recipients", requestBody, ct, Logger, ProviderName, "Tokenise").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<ChipperCashTokenisationProvider.ChipperCashRecipientResponse>(body);
            if (resp is null || string.IsNullOrEmpty(resp.Id))
                throw new BhenguPaymentException(ProviderName, "Chipper Cash recipient enrolment returned no id", "no_recipient_id");

            Logger.LogInformation("Chipper saved recipient: {Id} customer={Customer}", resp.Id, request.CustomerId);

            var method = new PaymentMethod
            {
                Token = resp.Id,
                CustomerId = request.CustomerId,
                Kind = PaymentMethodKind.MobileMoney,
                Brand = resp.Network ?? network,
                Last4 = msisdn.Length > 4 ? msisdn[^4..] : msisdn,
                DisplayName = request.DisplayName,
                IsDefault = request.SetAsDefault,
                CreatedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "tokenise", method, ct).ConfigureAwait(false);
            return method;
        }, ct);
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        return await _cache.GetAsync<T>(BuildCacheKey(idempotencyKey, operation), ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return;
        await _cache.SetAsync(BuildCacheKey(idempotencyKey, operation), value, s_idempotencyTtl, ct).ConfigureAwait(false);
    }

    private static string BuildCacheKey(string idempotencyKey, string operation)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant();
        return $"chippercash:idem:{operation}:{hash}";
    }
}

/// <summary>Shared HTTP plumbing for the Chipper Cash tokenisation providers.</summary>
internal static class ChipperCashHttp
{
    public static async Task<string> SendAsync(
        HttpClient httpClient,
        ChipperCashOptions options,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        ILogger logger,
        string providerName,
        string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        var json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        if (body is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(options.ApiSecret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json + "." + timestamp))).ToLowerInvariant();
        req.Headers.TryAddWithoutValidation("X-Chipper-Signature", signature);
        req.Headers.TryAddWithoutValidation("X-Chipper-Timestamp", timestamp);
        if (!string.IsNullOrWhiteSpace(options.MerchantId))
            req.Headers.TryAddWithoutValidation("X-Chipper-Merchant-Id", options.MerchantId);

        var response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(providerName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Chipper {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(providerName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(providerName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
