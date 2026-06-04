// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

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
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paymob.Providers;

/// <summary>
/// Paymob implementation of <see cref="ITokenisationProvider"/> backed by Paymob's
/// <c>/api/acceptance/tokenization</c> endpoint and Saved-Card API.
/// </summary>
/// <remarks>
/// <para>Paymob's tokenisation model returns an opaque <c>card_token</c> + an associated
/// <c>card_subtype</c>/<c>masked_pan</c> after the payer's first successful 3DS-cleared charge
/// — there is no server-side card-on-file create endpoint. For SAQ-D merchants that legitimately
/// handle raw PAN, this provider drives a tokenisation pre-auth charge to surface the token, then
/// voids the underlying transaction. Callers that cannot legally handle raw PAN should instead use
/// Paymob's iframe with <c>save_card_token=true</c> and call <see cref="GetPaymentMethodAsync"/>
/// with the token surfaced via webhook.</para>
/// </remarks>
public sealed class PaymobTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaymobOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paymob;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public PaymobTokenisationProvider(
        HttpClient httpClient,
        IOptions<PaymobOptions> options,
        ILogger<PaymobTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaymobOptions.ApiKey)} is required");

        PaymobHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync<PaymentMethod?>("get_payment_method", async () =>
        {
            try
            {
                var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
                var responseBody = await PaymobHttpClient.SendAsync(
                    _httpClient, Logger, HttpMethod.Get,
                    $"api/acceptance/saved_cards/{Uri.EscapeDataString(token)}?auth_token={Uri.EscapeDataString(authToken)}",
                    null, "GetPaymentMethod", ct).ConfigureAwait(false);

                var response = JsonSerializer.Deserialize<PaymobSavedCard>(responseBody, PaymobHttpClient.Json);
                if (response is null || string.IsNullOrEmpty(response.CardToken))
                    return null;

                return Map(response);
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

        List<PaymobSavedCard>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.list"))
        {
            try
            {
                var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
                var path = $"api/acceptance/saved_cards?identifier={Uri.EscapeDataString(customerId)}&auth_token={Uri.EscapeDataString(authToken)}";
                var responseBody = await PaymobHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Get, path, null, "ListPaymentMethods", ct).ConfigureAwait(false);

                var response = JsonSerializer.Deserialize<PaymobSavedCardList>(responseBody, PaymobHttpClient.Json);
                items = response?.Results;
            }
            catch (Exception)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var card in items)
        {
            if (string.IsNullOrEmpty(card.CardToken)) continue;
            ct.ThrowIfCancellationRequested();
            yield return Map(card);
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
                var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
                await PaymobHttpClient.SendAsync(
                    _httpClient, Logger, HttpMethod.Delete,
                    $"api/acceptance/saved_cards/{Uri.EscapeDataString(token)}?auth_token={Uri.EscapeDataString(authToken)}",
                    null, "DeletePaymentMethod", ct).ConfigureAwait(false);
                return true;
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return false;
            }
        }, ct);
    }

    internal static PaymentMethod Map(PaymobSavedCard card) => new()
    {
        Token = card.CardToken ?? string.Empty,
        CustomerId = card.Identifier,
        Kind = PaymentMethodKind.Card,
        Brand = card.CardSubtype ?? card.CardSchema,
        Last4 = LastFour(card.MaskedPan),
        ExpiryMonth = int.TryParse(card.ExpiryMonth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var em) ? em : null,
        ExpiryYear = int.TryParse(card.ExpiryYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ey) ? ey : null,
        IsDefault = card.IsDefault ?? false,
        CreatedAt = card.CreatedAt
    };

    internal static string? LastFour(string? value)
        => value is { Length: >= 4 } v ? v[^4..] : value;

    // === Paymob API shapes (internal, shared with raw-card variant) ===

    internal sealed class PaymobTokenisationResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("card_token")] public string? CardToken { get; set; }
        [JsonPropertyName("masked_pan")] public string? MaskedPan { get; set; }
        [JsonPropertyName("card_subtype")] public string? CardSubtype { get; set; }
        [JsonPropertyName("card_schema")] public string? CardSchema { get; set; }
    }

    internal sealed class PaymobSavedCard
    {
        [JsonPropertyName("card_token")] public string? CardToken { get; set; }
        [JsonPropertyName("identifier")] public string? Identifier { get; set; }
        [JsonPropertyName("masked_pan")] public string? MaskedPan { get; set; }
        [JsonPropertyName("card_subtype")] public string? CardSubtype { get; set; }
        [JsonPropertyName("card_schema")] public string? CardSchema { get; set; }
        [JsonPropertyName("expiry_month")] public string? ExpiryMonth { get; set; }
        [JsonPropertyName("expiry_year")] public string? ExpiryYear { get; set; }
        [JsonPropertyName("is_default")] public bool? IsDefault { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }

    internal sealed class PaymobSavedCardList
    {
        [JsonPropertyName("results")] public List<PaymobSavedCard>? Results { get; set; }
    }
}

/// <summary>
/// PCI-DSS SAQ-D-scope Paymob raw-card tokenisation. Sends raw PAN to Paymob's
/// <c>/api/acceptance/tokenization</c> endpoint and returns the reusable card token.
/// </summary>
public sealed class PaymobRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaymobOptions _options;
    private readonly PaymobIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paymob;

    /// <summary>Construct a raw-card tokenisation provider.</summary>
    public PaymobRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<PaymobOptions> options,
        ILogger<PaymobRawCardTokenisationProvider> logger,
        PaymobIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaymobOptions.ApiKey)} is required");

        PaymobHttpClient.ConfigureClient(_httpClient, _options);
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
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);

            var body = new
            {
                auth_token = authToken,
                card_number = request.Card.CardNumber,
                card_holdername = request.Card.CardholderName,
                expiry_year = request.Card.ExpiryYear.ToString(CultureInfo.InvariantCulture),
                expiry_month = request.Card.ExpiryMonth.ToString("D2", CultureInfo.InvariantCulture),
                cvn = request.Card.Cvv,
                identifier = request.CustomerId,
                set_as_default = request.SetAsDefault,
                merchant_identifier = request.DisplayName
            };

            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "api/acceptance/tokenization", body, "Tokenise", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaymobTokenisationProvider.PaymobTokenisationResponse>(responseBody, PaymobHttpClient.Json);

            var token = response?.CardToken ?? response?.Token;
            if (string.IsNullOrWhiteSpace(token))
                throw new PaymentDeclinedException(ProviderName, "no_card_token",
                    "Paymob did not return a card_token — card may have been declined or 3DS authentication is required.");

            Logger.LogInformation("Paymob tokenised card for customer {CustomerId} → token={Token}",
                request.CustomerId, token);

            return new PaymentMethod
            {
                Token = token,
                CustomerId = request.CustomerId,
                Kind = PaymentMethodKind.Card,
                Brand = response?.CardSubtype ?? response?.CardSchema,
                Last4 = PaymobTokenisationProvider.LastFour(response?.MaskedPan ?? request.Card.CardNumber),
                ExpiryMonth = request.Card.ExpiryMonth,
                ExpiryYear = request.Card.ExpiryYear,
                DisplayName = request.DisplayName,
                IsDefault = request.SetAsDefault,
                CreatedAt = DateTime.UtcNow
            };
        }, ct);
}
