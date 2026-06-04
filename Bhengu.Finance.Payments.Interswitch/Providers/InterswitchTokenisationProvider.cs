// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Interswitch.Providers;

/// <summary>
/// Interswitch implementation of <see cref="ITokenisationProvider"/>. Wraps Interswitch's
/// card-on-file endpoint <c>POST /payment/v2/save-card</c> and the companion fetch / list / delete
/// endpoints. The resulting <see cref="PaymentMethod.Token"/> is the Interswitch <em>cardToken</em>
/// (sometimes called <c>cardId</c>) which can later be passed as
/// <see cref="Bhengu.Finance.Payments.Core.Models.PaymentRequest.PaymentMethodToken"/>.
/// </summary>
/// <remarks>
/// <para>Interswitch's tokenisation flow normally runs on the hosted Quickteller checkout — the
/// merchant rarely handles raw PAN. This server-side path exists for SAQ-D merchants and the
/// (less common) merchant-initiated save flow.</para>
/// </remarks>
public sealed class InterswitchTokenisationProvider : ITokenisationProvider
{
    private readonly InterswitchHttpClient _http;
    private readonly InterswitchOptions _options;
    private readonly ILogger<InterswitchTokenisationProvider> _logger;
    private readonly InterswitchIdempotencyCache _idempotency;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Interswitch;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public InterswitchTokenisationProvider(
        HttpClient httpClient,
        IOptions<InterswitchOptions> options,
        ILogger<InterswitchTokenisationProvider> logger,
        InterswitchIdempotencyCache idempotency)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientSecret)} is required");

        _http = new InterswitchHttpClient(httpClient, _options, _logger);
    }

    /// <inheritdoc />
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, "tokenise",
            () => TokeniseCoreAsync(request, ct), ct);
    }

    private async Task<PaymentMethod> TokeniseCoreAsync(TokeniseRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise");
        var outcome = BhenguPaymentDiagnostics.Outcomes.Pending;
        try
        {
            if (string.IsNullOrWhiteSpace(request.CustomerId))
                throw new PaymentDeclinedException(ProviderName, "missing_customer",
                    "Interswitch save-card requires TokeniseRequest.CustomerId.");

            var body = new
            {
                customerId = request.CustomerId,
                cardPan = request.Card.CardNumber,
                expiryDate = $"{request.Card.ExpiryMonth:D2}{request.Card.ExpiryYear % 100:D2}",
                cvv2 = request.Card.Cvv,
                pinBlock = (string?)null,
                cardHolderName = request.Card.CardholderName,
                alias = request.DisplayName,
                defaultCard = request.SetAsDefault
            };

            var json = await _http.SendAsync(HttpMethod.Post, "payment/v2/save-card", body, "TokeniseSaveCard", ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<InterswitchSavedCardResponse>(json, InterswitchHttpClient.Json)
                ?? throw new BhenguPaymentException(ProviderName, "Interswitch save-card returned an empty body", "empty_response");

            if (string.IsNullOrWhiteSpace(resp.CardToken))
                throw new PaymentDeclinedException(ProviderName, resp.ResponseCode ?? "no_token",
                    resp.ResponseDescription ?? "Interswitch did not return a card token.");

            outcome = BhenguPaymentDiagnostics.Outcomes.Success;
            return Map(resp, request.CustomerId, request.DisplayName, request.SetAsDefault);
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch (Exception) { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity?.SetOutcome(outcome);
        }
    }

    /// <inheritdoc />
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "get_payment_method");
        try
        {
            var path = $"payment/v2/cards/{Uri.EscapeDataString(token)}";
            var json = await _http.SendAsync(HttpMethod.Get, path, null, "GetPaymentMethod", ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<InterswitchSavedCardResponse>(json, InterswitchHttpClient.Json);
            if (resp is null || string.IsNullOrEmpty(resp.CardToken)) return null;
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return Map(resp, resp.CustomerId, null, false);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_payment_methods");
        var path = $"payment/v2/customers/{Uri.EscapeDataString(customerId)}/cards";
        var json = await _http.SendAsync(HttpMethod.Get, path, null, "ListPaymentMethods", ct).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<InterswitchSavedCardListResponse>(json, InterswitchHttpClient.Json);
        if (list?.Cards is null || list.Cards.Count == 0)
        {
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return Array.Empty<PaymentMethod>();
        }
        var result = new List<PaymentMethod>(list.Cards.Count);
        foreach (var c in list.Cards) result.Add(Map(c, customerId, null, c.DefaultCard));
        activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "delete_payment_method");
        try
        {
            var path = $"payment/v2/cards/{Uri.EscapeDataString(token)}";
            await _http.SendAsync(HttpMethod.Delete, path, null, "DeletePaymentMethod", ct).ConfigureAwait(false);
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return true;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return false;
        }
    }

    private static PaymentMethod Map(InterswitchSavedCardResponse src, string? customerId, string? displayName, bool isDefault)
    {
        int? em = null;
        int? ey = null;
        if (!string.IsNullOrEmpty(src.ExpiryDate) && src.ExpiryDate.Length == 4
            && int.TryParse(src.ExpiryDate[..2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(src.ExpiryDate[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            em = m;
            ey = 2000 + y;
        }
        return new PaymentMethod
        {
            Token = src.CardToken ?? string.Empty,
            CustomerId = customerId ?? src.CustomerId,
            Kind = PaymentMethodKind.Card,
            Brand = src.CardScheme ?? src.CardBrand,
            Last4 = string.IsNullOrEmpty(src.MaskedPan) || src.MaskedPan.Length < 4
                ? null
                : src.MaskedPan[^4..],
            ExpiryMonth = em,
            ExpiryYear = ey,
            DisplayName = displayName ?? src.Alias,
            IsDefault = isDefault || src.DefaultCard,
            CreatedAt = src.CreatedAt
        };
    }

    // === Interswitch API shapes (internal) ===

    private sealed class InterswitchSavedCardResponse
    {
        [JsonPropertyName("cardToken")] public string? CardToken { get; set; }
        [JsonPropertyName("customerId")] public string? CustomerId { get; set; }
        [JsonPropertyName("maskedPan")] public string? MaskedPan { get; set; }
        [JsonPropertyName("expiryDate")] public string? ExpiryDate { get; set; }
        [JsonPropertyName("cardBrand")] public string? CardBrand { get; set; }
        [JsonPropertyName("cardScheme")] public string? CardScheme { get; set; }
        [JsonPropertyName("alias")] public string? Alias { get; set; }
        [JsonPropertyName("defaultCard")] public bool DefaultCard { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class InterswitchSavedCardListResponse
    {
        [JsonPropertyName("cards")] public List<InterswitchSavedCardResponse>? Cards { get; set; }
    }
}
