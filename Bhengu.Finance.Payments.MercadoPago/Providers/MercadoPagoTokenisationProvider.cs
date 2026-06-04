// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MercadoPago.Providers;

/// <summary>
/// Mercado Pago card-tokenisation provider. Wraps the <c>/v1/card_tokens</c> +
/// <c>/v1/customers</c> + <c>/v1/customers/{id}/cards</c> endpoints.
/// </summary>
/// <remarks>
/// Server-side tokenisation transits raw PAN through your server — only use this provider if your
/// merchant is already PCI-DSS Level-1 SAQ-D. Otherwise prefer Mercado Pago Checkout Bricks
/// (client-side) which returns a card_token to your server without your servers seeing PAN.
/// <para>
/// Card tokens by themselves are short-lived (~15 minutes). To persist a card for re-use, the
/// <c>TokeniseAsync</c> flow creates (or re-uses) a Mercado Pago customer and attaches the card
/// to that customer via <c>/v1/customers/{id}/cards</c>, which yields a long-lived card id.
/// </para>
/// </remarks>
public sealed class MercadoPagoTokenisationProvider : ITokenisationProvider
{
    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly MercadoPagoOptions _options;
    private readonly ILogger<MercadoPagoTokenisationProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.MercadoPago;

    /// <summary>Create a new Mercado Pago tokenisation provider bound to the supplied HTTP client and options.</summary>
    public MercadoPagoTokenisationProvider(
        HttpClient httpClient,
        IOptions<MercadoPagoOptions> options,
        ILogger<MercadoPagoTokenisationProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MercadoPagoOptions.AccessToken)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mercadopago.com");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    /// <inheritdoc />
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Mercado Pago: card id is scoped under a customer. We don't have the customer id at this
        // entry point, but the search endpoint accepts a card id directly.
        try
        {
            var raw = await MercadoPagoHttp.SendAsync(_httpClient, _logger, ProviderName, HttpMethod.Get, $"/v1/customers/cards/{Uri.EscapeDataString(token)}", body: null, ct, "GetCard", idempotencyKey: null).ConfigureAwait(false);
            var card = DeserialiseOrThrow<MercadoPagoCard>(raw, "GetCard");
            return MapCard(card, card.CustomerId, displayName: null, isDefault: false);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        var raw = await MercadoPagoHttp.SendAsync(_httpClient, _logger, ProviderName, HttpMethod.Get, $"/v1/customers/{Uri.EscapeDataString(customerId)}/cards", body: null, ct, "ListCards", idempotencyKey: null).ConfigureAwait(false);

        MercadoPagoCard[] cards;
        try
        {
            cards = JsonSerializer.Deserialize<MercadoPagoCard[]>(raw) ?? Array.Empty<MercadoPagoCard>();
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(ProviderName, "Failed to parse Mercado Pago ListCards response", innerException: ex);
        }

        foreach (var c in cards)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapCard(c, customerId, displayName: null, isDefault: false);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // The DELETE endpoint requires the customer id. Look up first.
        var method = await GetPaymentMethodAsync(token, ct).ConfigureAwait(false);
        if (method is null || string.IsNullOrWhiteSpace(method.CustomerId))
            return false;

        try
        {
            await MercadoPagoHttp.SendAsync(_httpClient, _logger, ProviderName, HttpMethod.Delete, $"/v1/customers/{Uri.EscapeDataString(method.CustomerId)}/cards/{Uri.EscapeDataString(token)}", body: null, ct, "DeleteCard", idempotencyKey: null).ConfigureAwait(false);
            return true;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return false;
        }
    }

    internal static PaymentMethod MapCard(MercadoPagoCard card, string? customerId, string? displayName, bool isDefault)
    {
        return new PaymentMethod
        {
            Token = card.Id ?? string.Empty,
            CustomerId = customerId ?? card.CustomerId,
            Kind = PaymentMethodKind.Card,
            Brand = card.PaymentMethod?.Name ?? card.PaymentMethod?.Id,
            Last4 = card.LastFourDigits,
            ExpiryMonth = card.ExpirationMonth,
            ExpiryYear = card.ExpirationYear,
            DisplayName = displayName,
            IsDefault = isDefault,
            CreatedAt = TryParseDate(card.DateCreated)
        };
    }

    private static DateTime? TryParseDate(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : (DateTime?)null;

    internal static T DeserialiseOrThrow<T>(string raw, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw)
                ?? throw new BhenguPaymentException(ProviderNames.MercadoPago, $"Mercado Pago {operation} returned empty body");
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(ProviderNames.MercadoPago, $"Failed to parse Mercado Pago {operation} response", innerException: ex);
        }
    }

    // === Mercado Pago API response shapes (internal, shared with raw-card variant) ===

    internal sealed class MercadoPagoCardToken
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("first_six_digits")] public string? FirstSixDigits { get; set; }
        [JsonPropertyName("last_four_digits")] public string? LastFourDigits { get; set; }
    }

    internal sealed class MercadoPagoCustomer
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("first_name")] public string? FirstName { get; set; }
        [JsonPropertyName("last_name")] public string? LastName { get; set; }
    }

    internal sealed class MercadoPagoCard
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
        [JsonPropertyName("first_six_digits")] public string? FirstSixDigits { get; set; }
        [JsonPropertyName("last_four_digits")] public string? LastFourDigits { get; set; }
        [JsonPropertyName("expiration_month")] public int? ExpirationMonth { get; set; }
        [JsonPropertyName("expiration_year")] public int? ExpirationYear { get; set; }
        [JsonPropertyName("payment_method")] public MercadoPagoCardPaymentMethod? PaymentMethod { get; set; }
        [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
    }

    internal sealed class MercadoPagoCardPaymentMethod
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}

/// <summary>Shared HTTP send helper for Mercado Pago vault providers.</summary>
internal static class MercadoPagoHttp
{
    public static async Task<string> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        string providerName,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        string operation,
        string? idempotencyKey)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, MercadoPagoTokenisationProvider.WriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (method == HttpMethod.Post)
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(providerName, "HTTP request to Mercado Pago failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(providerName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Mercado Pago {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(providerName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(providerName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}

/// <summary>
/// PCI-DSS SAQ-D-scope Mercado Pago tokenisation. Sends raw PAN to <c>/v1/card_tokens</c>, then
/// attaches the resulting card to a vault customer for long-lived re-use. Strongly prefer Mercado
/// Pago Checkout Bricks (client-side) — only use this where the merchant is PCI-DSS SAQ-D.
/// </summary>
public sealed class MercadoPagoRawCardTokenisationProvider : IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly MercadoPagoOptions _options;
    private readonly ILogger<MercadoPagoRawCardTokenisationProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.MercadoPago;

    /// <summary>Create a new Mercado Pago raw-card tokenisation provider.</summary>
    public MercadoPagoRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<MercadoPagoOptions> options,
        ILogger<MercadoPagoRawCardTokenisationProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MercadoPagoOptions.AccessToken)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mercadopago.com");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    /// <inheritdoc />
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) Mint a short-lived card token from raw PAN.
        var tokenBody = new
        {
            card_number = request.Card.CardNumber,
            expiration_month = request.Card.ExpiryMonth,
            expiration_year = request.Card.ExpiryYear,
            security_code = request.Card.Cvv,
            cardholder = new
            {
                name = request.Card.CardholderName
            }
        };

        var tokenRaw = await MercadoPagoHttp.SendAsync(_httpClient, _logger, ProviderName, HttpMethod.Post, "/v1/card_tokens", tokenBody, ct, "CreateCardToken", request.IdempotencyKey).ConfigureAwait(false);
        var token = MercadoPagoTokenisationProvider.DeserialiseOrThrow<MercadoPagoTokenisationProvider.MercadoPagoCardToken>(tokenRaw, "CreateCardToken");

        // 2) Ensure a vault customer exists.
        var customerId = request.CustomerId;
        if (string.IsNullOrWhiteSpace(customerId))
        {
            var customerBody = new
            {
                email = request.DisplayName is { Length: > 0 } d ? d : $"vault-{Guid.NewGuid():N}@mercadopago.bhengu",
                first_name = request.Card.CardholderName
            };
            var customerRaw = await MercadoPagoHttp.SendAsync(_httpClient, _logger, ProviderName, HttpMethod.Post, "/v1/customers", customerBody, ct, "CreateCustomer", request.IdempotencyKey).ConfigureAwait(false);
            var customer = MercadoPagoTokenisationProvider.DeserialiseOrThrow<MercadoPagoTokenisationProvider.MercadoPagoCustomer>(customerRaw, "CreateCustomer");
            customerId = customer.Id;
        }

        // 3) Persist the card against the customer for a long-lived card id.
        var cardBody = new { token = token.Id };
        var cardRaw = await MercadoPagoHttp.SendAsync(_httpClient, _logger, ProviderName, HttpMethod.Post, $"/v1/customers/{Uri.EscapeDataString(customerId ?? string.Empty)}/cards", cardBody, ct, "AttachCard", request.IdempotencyKey).ConfigureAwait(false);
        var card = MercadoPagoTokenisationProvider.DeserialiseOrThrow<MercadoPagoTokenisationProvider.MercadoPagoCard>(cardRaw, "AttachCard");

        _logger.LogInformation("Mercado Pago card vaulted: cardId={CardId} customerId={CustomerId}", card.Id, customerId);

        return MercadoPagoTokenisationProvider.MapCard(card, customerId, request.DisplayName, request.SetAsDefault);
    }
}
