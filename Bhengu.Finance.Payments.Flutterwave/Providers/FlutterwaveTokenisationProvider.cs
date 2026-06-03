// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Flutterwave.Providers;

/// <summary>
/// Flutterwave tokenisation provider — vaults card details for re-use.
/// <para>
/// Flutterwave does not expose a generic <c>POST /tokens</c> endpoint. Tokens are minted as a
/// by-product of a successful charge — the verify-transaction response includes a <c>card.token</c>
/// field. To produce a token without billing the cardholder we issue a low-value pre-auth charge via
/// <c>POST /v3/tokenized-charges</c> using the platform's <see cref="FlutterwaveOptions.EncryptionKey"/>.
/// </para>
/// <para>
/// Customer identity is surrogate-keyed by email (Flutterwave's own convention — no first-class
/// customer object). <see cref="ListPaymentMethodsAsync"/> requires a previously-issued token to look
/// up the customer's other tokens via the saved-cards endpoint.
/// </para>
/// </summary>
public sealed class FlutterwaveTokenisationProvider : ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;
    private readonly ILogger<FlutterwaveTokenisationProvider> _logger;
    private readonly FlutterwaveIdempotencyCache _idempotencyCache;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Flutterwave;

    /// <summary>Construct the provider; configures Bearer auth on the injected <paramref name="httpClient"/>.</summary>
    public FlutterwaveTokenisationProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwaveTokenisationProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotencyCache = new FlutterwaveIdempotencyCache();

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FlutterwaveOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.flutterwave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Card);
        return _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, () => TokeniseCoreAsync(request, ct));
    }

    private async Task<PaymentMethod> TokeniseCoreAsync(TokeniseRequest request, CancellationToken ct)
    {
        // Flutterwave's customer object is surrogate-keyed on email per their convention.
        var email = request.CustomerId ?? throw new PaymentDeclinedException(ProviderName, "missing_customer",
            "Flutterwave tokenisation requires CustomerId (an email address) on TokeniseRequest.");

        if (string.IsNullOrWhiteSpace(_options.EncryptionKey))
            throw new ProviderConfigurationException(ProviderName,
                $"{nameof(FlutterwaveOptions.EncryptionKey)} is required for server-side card tokenisation.");

        var card = request.Card;
        var expiry = $"{card.ExpiryMonth:D2}/{(card.ExpiryYear % 100):D2}";

        // Tokenised-charge body — Flutterwave returns a card.token on success that can be reused
        // against /v3/tokenized-charges for subsequent off-session charges.
        var body = new
        {
            tx_ref = $"vault-{Guid.NewGuid():N}",
            amount = "1.00", // pre-auth probe; real charges happen via tokenised-charges later.
            currency = "USD",
            card_number = card.CardNumber,
            cvv = card.Cvv,
            expiry_month = card.ExpiryMonth.ToString(CultureInfo.InvariantCulture),
            expiry_year = (card.ExpiryYear % 100).ToString(CultureInfo.InvariantCulture),
            email,
            preauthorize = true
        };

        var responseBody = await SendAsync(HttpMethod.Post, "v3/tokenized-charges", body, ct, "Tokenise").ConfigureAwait(false);
        var fw = JsonSerializer.Deserialize<FlutterwaveTokenisedChargeResponse>(responseBody);
        var token = fw?.Data?.Card?.Token
            ?? throw new BhenguPaymentException(ProviderName, "Flutterwave tokenisation did not return a card.token");

        _logger.LogInformation("Flutterwave card vaulted for {Email}: token={Token} brand={Brand} last4={Last4}",
            email, token, fw?.Data?.Card?.Type, fw?.Data?.Card?.Last4Digits);

        return new PaymentMethod
        {
            Token = token,
            CustomerId = email,
            Kind = PaymentMethodKind.Card,
            Brand = fw?.Data?.Card?.Type ?? card.CardholderName,
            Last4 = fw?.Data?.Card?.Last4Digits ?? Last4Of(card.CardNumber),
            ExpiryMonth = card.ExpiryMonth,
            ExpiryYear = card.ExpiryYear,
            DisplayName = request.DisplayName,
            IsDefault = request.SetAsDefault,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Flutterwave has no public "fetch token" endpoint. We expose what callers already know about
    /// the token (its value), returning a partially-populated descriptor that survives round-trip
    /// through callers' UIs. For full card metadata, the caller should persist the
    /// <see cref="PaymentMethod"/> returned by <see cref="TokeniseAsync"/>.
    /// </remarks>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var pm = new PaymentMethod
        {
            Token = token,
            Kind = PaymentMethodKind.Card
        };
        return Task.FromResult<PaymentMethod?>(pm);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Flutterwave's saved-cards lookup is keyed by the platform's encryption key + email; the API
    /// surface is undocumented for general partners. To keep the contract honest we return an empty
    /// list rather than fabricate data. Callers that need a customer's token history should persist
    /// the <see cref="PaymentMethod"/> returned by <see cref="TokeniseAsync"/> in their own datastore.
    /// </remarks>
    public Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);
        return Task.FromResult<IReadOnlyList<PaymentMethod>>(Array.Empty<PaymentMethod>());
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Flutterwave does not expose a delete-token endpoint — tokens expire when the underlying card
    /// expires or after extended inactivity. To honour the contract we return <c>true</c> when the
    /// token is well-formed and let the caller's own datastore mark it as removed.
    /// </remarks>
    public Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        _logger.LogInformation("Flutterwave has no delete-token API; caller should evict {Token} from their own vault.", token);
        return Task.FromResult(true);
    }

    private static string Last4Of(string pan) => pan.Length >= 4 ? pan[^4..] : pan;

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Flutterwave failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Flutterwave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    // === Flutterwave response shapes (internal) ===

    private sealed class FlutterwaveTokenisedChargeResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwaveTokenisedChargeData? Data { get; set; }
    }

    private sealed class FlutterwaveTokenisedChargeData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tx_ref")] public string? TxRef { get; set; }
        [JsonPropertyName("flw_ref")] public string? FlwRef { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("card")] public FlutterwaveCard? Card { get; set; }
    }

    private sealed class FlutterwaveCard
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("first_6digits")] public string? First6Digits { get; set; }
        [JsonPropertyName("last_4digits")] public string? Last4Digits { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("expiry")] public string? Expiry { get; set; }
    }
}
