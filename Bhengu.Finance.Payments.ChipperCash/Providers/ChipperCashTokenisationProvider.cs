// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ChipperCash.Providers;

/// <summary>
/// Chipper Cash implementation of <see cref="ITokenisationProvider"/>. Chipper does not vault card
/// PANs — instead it stores <em>saved recipients</em> (named mobile-money / wallet destinations
/// keyed by MSISDN and network). The returned <see cref="PaymentMethod.Token"/> is the saved
/// recipient identifier; pass it as <see cref="Core.Models.PayoutRequest.DestinationToken"/> on
/// subsequent disbursements to skip the per-call beneficiary enrolment fields.
/// </summary>
/// <remarks>
/// Chipper does not expose a card vault on the standard merchant tier — the
/// <see cref="TokeniseRequest.Card"/> details are NOT sent upstream; only the
/// <see cref="TokeniseRequest.DisplayName"/> and the MSISDN/network resolved from the request's
/// metadata are persisted. Pass MSISDN via <c>request.DisplayName</c>'s metadata channel by
/// supplying <c>msisdn</c> + <c>network</c> on the request's downstream
/// <see cref="Core.Models.PayoutRequest.Metadata"/> — see provider docs.
/// </remarks>
public sealed class ChipperCashTokenisationProvider : ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly ChipperCashOptions _options;
    private readonly ILogger<ChipperCashTokenisationProvider> _logger;
    private readonly IBhenguDistributedCache _cache;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.ChipperCash;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public ChipperCashTokenisationProvider(
        HttpClient httpClient,
        IOptions<ChipperCashOptions> options,
        ILogger<ChipperCashTokenisationProvider> logger,
        IBhenguDistributedCache? cache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise");
        var outcomeTag = BhenguPaymentDiagnostics.Outcomes.Error;
        try
        {
            var cached = await TryGetCachedAsync<PaymentMethod>(request.IdempotencyKey, "tokenise", ct).ConfigureAwait(false);
            if (cached is not null)
            {
                outcomeTag = BhenguPaymentDiagnostics.Outcomes.Success;
                return cached;
            }

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

            var body = await SendAsync(HttpMethod.Post, "v1/recipients", requestBody, ct, "Tokenise").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<ChipperCashRecipientResponse>(body);
            if (resp is null || string.IsNullOrEmpty(resp.Id))
                throw new BhenguPaymentException(ProviderName, "Chipper Cash recipient enrolment returned no id", "no_recipient_id");

            _logger.LogInformation("Chipper saved recipient: {Id} customer={Customer}", resp.Id, request.CustomerId);

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
            outcomeTag = BhenguPaymentDiagnostics.Outcomes.Success;
            return method;
        }
        catch (PaymentDeclinedException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcomeTag = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        finally
        {
            activity.SetOutcome(outcomeTag);
        }
    }

    /// <inheritdoc/>
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "get_payment_method");
        try
        {
            var body = await SendAsync(HttpMethod.Get, $"v1/recipients/{Uri.EscapeDataString(token)}", null, ct, "GetPaymentMethod").ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<ChipperCashRecipientResponse>(body);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return resp is null ? null : MapRecipient(resp);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_payment_methods");
        var body = await SendAsync(HttpMethod.Get, $"v1/customers/{Uri.EscapeDataString(customerId)}/recipients", null, ct, "ListPaymentMethods").ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<ChipperCashRecipientListResponse>(body);
        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
        if (list?.Recipients is null) return Array.Empty<PaymentMethod>();

        var result = new List<PaymentMethod>(list.Recipients.Count);
        foreach (var r in list.Recipients) result.Add(MapRecipient(r));
        return result;
    }

    /// <inheritdoc/>
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "delete_payment_method");
        try
        {
            await SendAsync(HttpMethod.Delete, $"v1/recipients/{Uri.EscapeDataString(token)}", null, ct, "DeletePaymentMethod").ConfigureAwait(false);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return true;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return false;
        }
    }

    private static PaymentMethod MapRecipient(ChipperCashRecipientResponse r) => new()
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

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        var json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        if (body is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.ApiSecret));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json + "." + timestamp))).ToLowerInvariant();
        req.Headers.TryAddWithoutValidation("X-Chipper-Signature", signature);
        req.Headers.TryAddWithoutValidation("X-Chipper-Timestamp", timestamp);
        if (!string.IsNullOrWhiteSpace(_options.MerchantId))
            req.Headers.TryAddWithoutValidation("X-Chipper-Merchant-Id", _options.MerchantId);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Chipper Cash failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Chipper {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
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

    private sealed class ChipperCashRecipientResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("customerId")] public string? CustomerId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("msisdn")] public string? Msisdn { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("isDefault")] public bool? IsDefault { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }

    private sealed class ChipperCashRecipientListResponse
    {
        [JsonPropertyName("recipients")] public List<ChipperCashRecipientResponse>? Recipients { get; set; }
    }
}
