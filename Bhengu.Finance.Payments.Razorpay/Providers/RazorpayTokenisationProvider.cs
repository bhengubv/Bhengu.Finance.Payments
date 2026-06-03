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
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay vault provider. Wraps Razorpay's <c>/v1/customers</c> + <c>/v1/tokens</c> endpoints —
/// when a customer doesn't already exist we create one, then attach a token to it.
/// </summary>
/// <remarks>
/// Razorpay's recommended flow is client-side tokenisation via Razorpay Standard Checkout — the
/// merchant SHOULD prefer that for SAQ-D-out compliance. This provider exists for server-side
/// flows where the merchant is already PCI-DSS Level-1 SAQ-D and for orchestrating token deletion
/// + listing from the merchant backend.
/// </remarks>
public sealed class RazorpayTokenisationProvider : ITokenisationProvider
{
    private readonly RazorpayHttpClient _http;
    private readonly ILogger<RazorpayTokenisationProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new tokenisation provider bound to the supplied HTTP client and options.</summary>
    public RazorpayTokenisationProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayTokenisationProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new RazorpayHttpClient(httpClient, options.Value, ProviderName, logger);
    }

    /// <inheritdoc />
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1) ensure a customer exists. Razorpay doesn't let tokens live unattached.
        var customerId = request.CustomerId;
        if (string.IsNullOrWhiteSpace(customerId))
        {
            var customerBody = new
            {
                name = request.Card.CardholderName,
                fail_existing = "0"
            };
            var customerRaw = await _http.SendAsync(HttpMethod.Post, "v1/customers", customerBody, ct, "CreateCustomer", request.IdempotencyKey).ConfigureAwait(false);
            var customer = RazorpayHttpClient.DeserialiseOrThrow<RazorpayCustomer>(customerRaw, ProviderName, "CreateCustomer");
            customerId = customer.Id;
        }

        // 2) attach a token. Razorpay tokens are scoped to a customer + method.
        var tokenBody = new
        {
            customer_id = customerId,
            method = "card",
            card = new
            {
                number = request.Card.CardNumber,
                name = request.Card.CardholderName,
                expiry_month = request.Card.ExpiryMonth,
                expiry_year = request.Card.ExpiryYear,
                cvv = request.Card.Cvv
            }
        };

        var tokenRaw = await _http.SendAsync(HttpMethod.Post, "v1/tokens", tokenBody, ct, "CreateToken", request.IdempotencyKey).ConfigureAwait(false);
        var token = RazorpayHttpClient.DeserialiseOrThrow<RazorpayToken>(tokenRaw, ProviderName, "CreateToken");

        _logger.LogInformation("Razorpay token created: {TokenId} for customer {CustomerId}", token.Id, customerId);

        return MapToken(token, customerId, request.DisplayName, request.SetAsDefault);
    }

    /// <inheritdoc />
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        try
        {
            var raw = await _http.GetAsync($"v1/tokens/{Uri.EscapeDataString(token)}", ct, "GetToken").ConfigureAwait(false);
            var t = RazorpayHttpClient.DeserialiseOrThrow<RazorpayToken>(raw, ProviderName, "GetToken");
            return MapToken(t, t.CustomerId, displayName: null, isDefault: false);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        var raw = await _http.GetAsync($"v1/customers/{Uri.EscapeDataString(customerId)}/tokens", ct, "ListTokens").ConfigureAwait(false);
        var collection = RazorpayHttpClient.DeserialiseOrThrow<RazorpayTokenCollection>(raw, ProviderName, "ListTokens");

        var methods = new List<PaymentMethod>(collection.Items?.Count ?? 0);
        if (collection.Items is not null)
            foreach (var t in collection.Items)
                methods.Add(MapToken(t, customerId, displayName: null, isDefault: false));

        return methods;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        // Razorpay's DELETE is scoped under a customer. The token-id alone isn't enough; we need to fetch first.
        var existing = await GetPaymentMethodAsync(token, ct).ConfigureAwait(false);
        if (existing is null || string.IsNullOrWhiteSpace(existing.CustomerId))
            return false;

        try
        {
            await _http.DeleteAsync($"v1/customers/{Uri.EscapeDataString(existing.CustomerId)}/tokens/{Uri.EscapeDataString(token)}", ct, "DeleteToken").ConfigureAwait(false);
            return true;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return false;
        }
    }

    private static PaymentMethod MapToken(RazorpayToken t, string? customerId, string? displayName, bool isDefault)
    {
        var kind = t.Method?.ToLowerInvariant() switch
        {
            "card" => PaymentMethodKind.Card,
            "upi" or "wallet" => PaymentMethodKind.Wallet,
            "netbanking" or "bank_account" => PaymentMethodKind.BankAccount,
            "emandate" or "nach" => PaymentMethodKind.Mandate,
            _ => PaymentMethodKind.Other
        };

        return new PaymentMethod
        {
            Token = t.Id ?? string.Empty,
            CustomerId = customerId ?? t.CustomerId,
            Kind = kind,
            Brand = t.Card?.Network ?? t.Wallet,
            Last4 = t.Card?.Last4 ?? t.BankAccount?.Last4,
            ExpiryMonth = t.Card?.ExpiryMonth,
            ExpiryYear = t.Card?.ExpiryYear,
            DisplayName = displayName,
            IsDefault = isDefault,
            CreatedAt = t.CreatedAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.CreatedAt.Value).UtcDateTime : null
        };
    }

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayCustomer
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class RazorpayToken
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
        [JsonPropertyName("method")] public string? Method { get; set; }
        [JsonPropertyName("wallet")] public string? Wallet { get; set; }
        [JsonPropertyName("card")] public RazorpayTokenCard? Card { get; set; }
        [JsonPropertyName("bank_account")] public RazorpayTokenBankAccount? BankAccount { get; set; }
        [JsonPropertyName("created_at")] public long? CreatedAt { get; set; }
    }

    private sealed class RazorpayTokenCard
    {
        [JsonPropertyName("last4")] public string? Last4 { get; set; }
        [JsonPropertyName("network")] public string? Network { get; set; }
        [JsonPropertyName("expiry_month")] public int? ExpiryMonth { get; set; }
        [JsonPropertyName("expiry_year")] public int? ExpiryYear { get; set; }
    }

    private sealed class RazorpayTokenBankAccount
    {
        [JsonPropertyName("last4")] public string? Last4 { get; set; }
        [JsonPropertyName("ifsc")] public string? Ifsc { get; set; }
    }

    private sealed class RazorpayTokenCollection
    {
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("items")] public List<RazorpayToken>? Items { get; set; }
    }
}
