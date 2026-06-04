// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Interswitch.Providers;

/// <summary>
/// READ-side Interswitch tokenisation provider — fetch / list / delete already-vaulted cards. The
/// PCI-impacting WRITE counterpart that accepts raw PAN lives in
/// <see cref="InterswitchRawCardTokenisationProvider"/>.
/// </summary>
public sealed class InterswitchTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    internal readonly InterswitchHttpClient Http;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Interswitch;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public InterswitchTokenisationProvider(
        HttpClient httpClient,
        IOptions<InterswitchOptions> options,
        ILogger<InterswitchTokenisationProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(opts.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(opts.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientSecret)} is required");

        Http = new InterswitchHttpClient(httpClient, opts, Logger);
    }

    /// <inheritdoc />
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync<PaymentMethod?>("get_payment_method", async () =>
        {
            try
            {
                var path = $"payment/v2/cards/{Uri.EscapeDataString(token)}";
                var json = await Http.SendAsync(HttpMethod.Get, path, null, "GetPaymentMethod", ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<InterswitchSavedCardResponse>(json, InterswitchHttpClient.Json);
                if (resp is null || string.IsNullOrEmpty(resp.CardToken)) return null;
                return Map(resp, resp.CustomerId, null, false);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(
        string customerId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);
        var path = $"payment/v2/customers/{Uri.EscapeDataString(customerId)}/cards";
        var json = await RunOperationAsync("list_payment_methods",
            () => Http.SendAsync(HttpMethod.Get, path, null, "ListPaymentMethods", ct), ct).ConfigureAwait(false);
        var list = JsonSerializer.Deserialize<InterswitchSavedCardListResponse>(json, InterswitchHttpClient.Json);
        if (list?.Cards is null) yield break;
        foreach (var c in list.Cards)
        {
            ct.ThrowIfCancellationRequested();
            yield return Map(c, customerId, null, c.DefaultCard);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("delete_payment_method", async () =>
        {
            try
            {
                var path = $"payment/v2/cards/{Uri.EscapeDataString(token)}";
                await Http.SendAsync(HttpMethod.Delete, path, null, "DeletePaymentMethod", ct).ConfigureAwait(false);
                return true;
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return false;
            }
        }, ct);
    }

    internal static PaymentMethod Map(InterswitchSavedCardResponse src, string? customerId, string? displayName, bool isDefault)
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

    internal sealed class InterswitchSavedCardResponse
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

    internal sealed class InterswitchSavedCardListResponse
    {
        [JsonPropertyName("cards")] public List<InterswitchSavedCardResponse>? Cards { get; set; }
    }
}
