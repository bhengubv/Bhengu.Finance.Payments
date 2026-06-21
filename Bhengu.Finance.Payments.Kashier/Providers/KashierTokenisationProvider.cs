// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Kashier.Providers;

/// <summary>
/// Kashier implementation of <see cref="ITokenisationProvider"/> backed by Kashier's saved-card token API
/// (<c>GET /tokens</c> to list, plus token deletion).
/// </summary>
/// <remarks>
/// Sources: www.kashier.io/docs/integration-guide ("Retrieve tokens": <c>GET /tokens?mid=...&amp;shopper_reference=...&amp;hash=...</c>;
/// "Delete Token") and developers.kashier.io. Token records expose <c>cardToken</c>, <c>cardHolderName</c>,
/// <c>maskedCard</c>, <c>expiry_month</c>, <c>expiry_year</c>.
/// </remarks>
public sealed class KashierTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Kashier;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public KashierTokenisationProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.MerchantId)} is required");

        KashierHttpClient.ConfigureClient(_httpClient, _options);
    }

    private string HashKey => string.IsNullOrWhiteSpace(_options.SecretKey) ? _options.ApiKey : _options.SecretKey;

    /// <inheritdoc/>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        // Kashier does not document a fetch-single-token endpoint; list the merchant's tokens and select by id.
        return RunOperationAsync<PaymentMethod?>("get_payment_method", async () =>
        {
            await foreach (var pm in ListAllAsync(shopperReference: null, ct).ConfigureAwait(false))
            {
                if (string.Equals(pm.Token, token, StringComparison.Ordinal))
                    return pm;
            }
            return null;
        }, ct);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);
        return ListAllAsync(customerId, ct);
    }

    private async IAsyncEnumerable<PaymentMethod> ListAllAsync(string? shopperReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<KashierCardData>? items;
        using (BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.list"))
        {
            // GET /tokens?mid=&shopper_reference=&hash=  (Kashier integration guide "Retrieve tokens").
            // UNVERIFIED: the exact preimage of the token-listing hash is not published; we reuse the documented
            // order-hash preimage with the shopper_reference in the orderId slot.
            var hash = KashierPaymentProvider.ComputeOrderHash(_options.MerchantId, shopperReference ?? string.Empty, string.Empty, _options.Currency, HashKey);
            var query = $"tokens?mid={Uri.EscapeDataString(_options.MerchantId)}";
            if (!string.IsNullOrWhiteSpace(shopperReference))
                query += $"&shopper_reference={Uri.EscapeDataString(shopperReference)}";
            query += $"&hash={hash}";

            var responseBody = await KashierHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, query, null, "ListPaymentMethods", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<KashierCardListResponse>(responseBody, KashierHttpClient.Json);
            items = response?.Response;
        }

        if (items is null) yield break;
        foreach (var c in items)
        {
            if (string.IsNullOrEmpty(c.CardToken)) continue;
            ct.ThrowIfCancellationRequested();
            yield return Map(c, null);
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
                // Kashier documents token deletion ("Delete Token") but does not publish the exact path.
                // UNVERIFIED: deletion path shape (mid + token) is a best-effort mapping, not sandbox-confirmed.
                var hash = KashierPaymentProvider.ComputeOrderHash(_options.MerchantId, token, string.Empty, _options.Currency, HashKey);
                var path = $"tokens/{Uri.EscapeDataString(token)}?mid={Uri.EscapeDataString(_options.MerchantId)}&hash={hash}";
                await KashierHttpClient.SendAsync(
                    _httpClient, Logger, HttpMethod.Delete, path, null, "DeletePaymentMethod", ct).ConfigureAwait(false);
                return true;
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return false;
            }
        }, ct);
    }

    internal static PaymentMethod Map(KashierCardData card, TokeniseRequest? request) => new()
    {
        Token = card.CardToken ?? string.Empty,
        CustomerId = card.ShopperReference ?? request?.CustomerId,
        Kind = PaymentMethodKind.Card,
        Brand = card.Brand,
        Last4 = card.Last4 ?? LastFour(card.MaskedCard),
        ExpiryMonth = int.TryParse(card.ExpiryMonth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var em) ? em : request?.Card.ExpiryMonth,
        ExpiryYear = int.TryParse(card.ExpiryYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ey) ? ey : request?.Card.ExpiryYear,
        DisplayName = card.CardHolderName ?? request?.DisplayName,
        IsDefault = card.IsDefault ?? request?.SetAsDefault ?? false,
        CreatedAt = card.CreatedAt ?? DateTime.UtcNow
    };

    private static string? LastFour(string? value) => value is { Length: >= 4 } v ? v[^4..] : value;

    internal sealed class KashierCardResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("response")] public KashierCardData? Response { get; set; }
    }

    internal sealed class KashierCardListResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("response")] public List<KashierCardData>? Response { get; set; }
    }

    // Field names per the Kashier token record: cardToken, cardHolderName, maskedCard, expiry_month, expiry_year.
    internal sealed class KashierCardData
    {
        [JsonPropertyName("cardToken")] public string? CardToken { get; set; }
        [JsonPropertyName("shopper_reference")] public string? ShopperReference { get; set; }
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("last4")] public string? Last4 { get; set; }
        [JsonPropertyName("maskedCard")] public string? MaskedCard { get; set; }
        [JsonPropertyName("expiry_month")] public string? ExpiryMonth { get; set; }
        [JsonPropertyName("expiry_year")] public string? ExpiryYear { get; set; }
        [JsonPropertyName("cardHolderName")] public string? CardHolderName { get; set; }
        [JsonPropertyName("isDefault")] public bool? IsDefault { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}

/// <summary>
/// PCI-DSS SAQ-D-scope Kashier tokenisation. Vaults a card against a shopper via Kashier's
/// <c>POST /tokenization</c> endpoint. Prefer Kashier hosted checkout / client-side tokenisation where possible.
/// </summary>
/// <remarks>
/// Field names (<c>merchantId</c>, <c>card_number</c>, <c>card_holder_name</c>, <c>ccv</c>, <c>expiry_month</c>,
/// <c>expiry_year</c>, <c>shopper_reference</c>, <c>hash</c>, <c>tokenValidity</c>) are per the asciisd/kashier
/// <c>TokenizationRequest</c> and the Kashier integration guide. Source path: asciisd <c>URL_PATH_TOKENIZATION = '/tokenization'</c>.
/// UNVERIFIED: Kashier's reference SDKs transmit the card data through a proprietary request cipher whose exact
/// transform is not publicly documented; this implementation sends the documented field names directly and has
/// never been sandbox-verified — treat as DocsOnly.
/// </remarks>
public sealed class KashierRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;
    private readonly KashierIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Kashier;

    /// <summary>Construct a raw-card tokenisation provider. Designed to be registered via DI.</summary>
    public KashierRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierRawCardTokenisationProvider> logger,
        KashierIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.MerchantId)} is required");

        KashierHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => TokeniseCoreAsync(request, ct), ct);
    }

    private Task<PaymentMethod> TokeniseCoreAsync(TokeniseRequest request, CancellationToken ct)
        => RunOperationAsync("tokenise_card", async () =>
        {
            var key = string.IsNullOrWhiteSpace(_options.SecretKey) ? _options.ApiKey : _options.SecretKey;
            // tokenValidity: "permanent" for card-on-file (default), "temporary" for a 10-minute checkout token.
            var body = new Dictionary<string, object?>
            {
                ["merchantId"] = _options.MerchantId,
                ["shopper_reference"] = request.CustomerId,
                ["card_holder_name"] = request.Card.CardholderName,
                ["card_number"] = request.Card.CardNumber,
                ["expiry_month"] = request.Card.ExpiryMonth.ToString("D2", CultureInfo.InvariantCulture),
                ["expiry_year"] = request.Card.ExpiryYear.ToString(CultureInfo.InvariantCulture),
                ["ccv"] = request.Card.Cvv,
                ["tokenValidity"] = "permanent",
                ["hash"] = KashierPaymentProvider.ComputeOrderHash(_options.MerchantId, request.CustomerId ?? string.Empty, string.Empty, _options.Currency, key)
            };

            var responseBody = await KashierHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "tokenization", body, "Tokenise", ct, request.IdempotencyKey).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<KashierTokenisationProvider.KashierCardResponse>(responseBody, KashierHttpClient.Json)?.Response
                ?? throw new BhenguPaymentException(ProviderName, "Kashier tokenisation returned no payload", "no_data");

            if (string.IsNullOrWhiteSpace(response.CardToken))
                throw new PaymentDeclinedException(ProviderName, "no_card_token",
                    "Kashier did not return a cardToken — card may have been declined.");

            Logger.LogInformation("Kashier tokenised card for shopper {Shopper} → token={Token}",
                request.CustomerId, response.CardToken);

            return KashierTokenisationProvider.Map(response, request);
        }, ct);
}
