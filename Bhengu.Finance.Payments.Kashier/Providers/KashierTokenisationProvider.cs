// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Kashier.Providers;

/// <summary>
/// Kashier implementation of <see cref="ITokenisationProvider"/> backed by Kashier's Card-Token
/// endpoint (<c>/cards</c>) for vaulting cards against a shopper.
/// </summary>
public sealed class KashierTokenisationProvider : ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;
    private readonly ILogger<KashierTokenisationProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Kashier;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public KashierTokenisationProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierTokenisationProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.MerchantId)} is required");

        KashierHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.get");
        try
        {
            var responseBody = await KashierHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, $"cards/{Uri.EscapeDataString(token)}", null, "GetPaymentMethod", ct).ConfigureAwait(false);
            var card = JsonSerializer.Deserialize<KashierCardResponse>(responseBody, KashierHttpClient.Json)?.Response;
            return card is null || string.IsNullOrEmpty(card.CardToken) ? null : Map(card, null);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);
        List<KashierCardData>? items;
        using (BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.list"))
        {
            var responseBody = await KashierHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, $"cards?shopperReference={Uri.EscapeDataString(customerId)}", null, "ListPaymentMethods", ct).ConfigureAwait(false);
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
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.delete");
        try
        {
            await KashierHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Delete, $"cards/{Uri.EscapeDataString(token)}", null, "DeletePaymentMethod", ct).ConfigureAwait(false);
            return true;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return false;
        }
    }

    internal static PaymentMethod Map(KashierCardData card, TokeniseRequest? request) => new()
    {
        Token = card.CardToken ?? string.Empty,
        CustomerId = card.ShopperReference ?? request?.CustomerId,
        Kind = PaymentMethodKind.Card,
        Brand = card.Brand,
        Last4 = card.Last4 ?? LastFour(card.MaskedPan),
        ExpiryMonth = int.TryParse(card.ExpiryMonth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var em) ? em : request?.Card.ExpiryMonth,
        ExpiryYear = int.TryParse(card.ExpiryYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ey) ? ey : request?.Card.ExpiryYear,
        DisplayName = card.DisplayName ?? request?.DisplayName,
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

    internal sealed class KashierCardData
    {
        [JsonPropertyName("cardToken")] public string? CardToken { get; set; }
        [JsonPropertyName("shopperReference")] public string? ShopperReference { get; set; }
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("last4")] public string? Last4 { get; set; }
        [JsonPropertyName("maskedPan")] public string? MaskedPan { get; set; }
        [JsonPropertyName("expiryMonth")] public string? ExpiryMonth { get; set; }
        [JsonPropertyName("expiryYear")] public string? ExpiryYear { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("isDefault")] public bool? IsDefault { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}

/// <summary>
/// PCI-DSS SAQ-D-scope Kashier tokenisation. Sends raw PAN to Kashier's <c>/cards</c> endpoint to
/// vault the card against a shopper. Prefer Kashier hosted checkout on the client where possible.
/// </summary>
public sealed class KashierRawCardTokenisationProvider : IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;
    private readonly ILogger<KashierRawCardTokenisationProvider> _logger;
    private readonly KashierIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Kashier;

    /// <summary>Construct a raw-card tokenisation provider. Designed to be registered via DI.</summary>
    public KashierRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierRawCardTokenisationProvider> logger,
        KashierIdempotencyCache idempotency)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

    private async Task<PaymentMethod> TokeniseCoreAsync(TokeniseRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise");

        var body = new
        {
            merchantId = _options.MerchantId,
            shopperReference = request.CustomerId,
            cardholderName = request.Card.CardholderName,
            cardNumber = request.Card.CardNumber,
            expiryMonth = request.Card.ExpiryMonth.ToString("D2", CultureInfo.InvariantCulture),
            expiryYear = request.Card.ExpiryYear.ToString(CultureInfo.InvariantCulture),
            cvv = request.Card.Cvv,
            setAsDefault = request.SetAsDefault,
            displayName = request.DisplayName
        };

        var responseBody = await KashierHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "cards", body, "Tokenise", ct, request.IdempotencyKey).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<KashierTokenisationProvider.KashierCardResponse>(responseBody, KashierHttpClient.Json)?.Response
            ?? throw new BhenguPaymentException(ProviderName, "Kashier tokenisation returned no payload", "no_data");

        if (string.IsNullOrWhiteSpace(response.CardToken))
            throw new PaymentDeclinedException(ProviderName, "no_card_token",
                "Kashier did not return a card_token — card may have been declined.");

        _logger.LogInformation("Kashier tokenised card for shopper {Shopper} → token={Token}",
            request.CustomerId, response.CardToken);

        return KashierTokenisationProvider.Map(response, request);
    }
}
