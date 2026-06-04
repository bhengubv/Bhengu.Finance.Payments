// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OPay.Providers;

/// <summary>
/// OPay implementation of <see cref="ITokenisationProvider"/>. OPay's wallet uses
/// <em>saved bank accounts</em> (NUBAN + bankCode) as the tokenisable payment-method primitive —
/// raw card storage is handled by OPay's hosted cashier and not exposed via this server-side
/// endpoint. The Bhengu adapter maps OPay's "saved bank" object onto the generic
/// <see cref="PaymentMethod"/> shape with <see cref="PaymentMethodKind.BankAccount"/>.
/// </summary>
/// <remarks>
/// <para>For card vaulting, prefer redirecting the payer to the OPay-hosted cashier so the
/// vaulted card lives in OPay's PCI-DSS scope, not yours; the resulting <c>paymentToken</c>
/// returned on the cashier callback is what you persist server-side.</para>
/// </remarks>
public sealed class OPayTokenisationProvider : ITokenisationProvider
{
    private readonly OPayHttpClient _http;
    private readonly OPayOptions _options;
    private readonly ILogger<OPayTokenisationProvider> _logger;
    private readonly OPayIdempotencyCache _idempotency;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.OPay;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public OPayTokenisationProvider(
        HttpClient httpClient,
        IOptions<OPayOptions> options,
        ILogger<OPayTokenisationProvider> logger,
        OPayIdempotencyCache idempotency)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.PublicKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.PublicKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.SecretKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.MerchantId)} is required");

        _http = new OPayHttpClient(httpClient, _options, _logger);
    }

    /// <inheritdoc />
    /// <remarks>
    /// OPay does not accept raw card details server-side. To register a saved bank account pass
    /// the NUBAN bank-account number via <see cref="CardDetails.CardNumber"/> and the 3-digit
    /// CBN bank code via <see cref="CardDetails.BillingAddressLine1"/>; this method then maps
    /// the request onto OPay's <c>cashier/savedBankAccount/register</c> endpoint.
    /// </remarks>
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
                    "OPay tokenisation requires TokeniseRequest.CustomerId (OPay user id).");

            var bankCode = request.Card.BillingAddressLine1
                ?? throw new PaymentDeclinedException(ProviderName, "missing_bank_code",
                    "OPay tokenisation requires bank code in CardDetails.BillingAddressLine1.");

            var body = new
            {
                publicKey = _options.PublicKey,
                country = _options.Country,
                sn = _options.MerchantId,
                userId = request.CustomerId,
                bankAccountNumber = request.Card.CardNumber,
                bankCode,
                accountHolderName = request.Card.CardholderName,
                alias = request.DisplayName,
                setAsDefault = request.SetAsDefault
            };

            var json = await _http.SendAsync(HttpMethod.Post, "api/v1/international/cashier/savedBankAccount/register",
                body, "Tokenise", ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<OPayResponse<OPaySavedBankAccount>>(json, OPayHttpClient.Json)
                ?? throw new BhenguPaymentException(ProviderName, "OPay save-bank-account returned an empty body", "empty_response");

            if (!string.Equals(resp.Code, "00000", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(resp.Data?.Token))
                throw new PaymentDeclinedException(ProviderName, resp.Code ?? "no_token", resp.Message);

            outcome = BhenguPaymentDiagnostics.Outcomes.Success;
            return Map(resp.Data, request.CustomerId, request.DisplayName, request.SetAsDefault);
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
            var body = new { publicKey = _options.PublicKey, sn = _options.MerchantId, token };
            var json = await _http.SendAsync(HttpMethod.Post,
                "api/v1/international/cashier/savedBankAccount/query", body, "GetPaymentMethod", ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<OPayResponse<OPaySavedBankAccount>>(json, OPayHttpClient.Json);
            if (resp?.Data?.Token is null) return null;
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return Map(resp.Data, resp.Data.UserId, null, resp.Data.IsDefault);
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
        var body = new { publicKey = _options.PublicKey, sn = _options.MerchantId, userId = customerId };
        var json = await _http.SendAsync(HttpMethod.Post,
            "api/v1/international/cashier/savedBankAccount/list", body, "ListPaymentMethods", ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPaySavedBankAccountList>>(json, OPayHttpClient.Json);
        activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

        if (resp?.Data?.Accounts is null || resp.Data.Accounts.Count == 0)
            return Array.Empty<PaymentMethod>();

        var result = new List<PaymentMethod>(resp.Data.Accounts.Count);
        foreach (var a in resp.Data.Accounts) result.Add(Map(a, customerId, null, a.IsDefault));
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "delete_payment_method");
        try
        {
            var body = new { publicKey = _options.PublicKey, sn = _options.MerchantId, token };
            var json = await _http.SendAsync(HttpMethod.Post,
                "api/v1/international/cashier/savedBankAccount/remove", body, "DeletePaymentMethod", ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<OPayResponse<object>>(json, OPayHttpClient.Json);
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return string.Equals(resp?.Code, "00000", StringComparison.Ordinal);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return false;
        }
    }

    private static PaymentMethod Map(OPaySavedBankAccount src, string? customerId, string? displayName, bool isDefault) => new()
    {
        Token = src.Token ?? string.Empty,
        CustomerId = customerId ?? src.UserId,
        Kind = PaymentMethodKind.BankAccount,
        Brand = src.BankName ?? src.BankCode,
        Last4 = string.IsNullOrEmpty(src.BankAccountNumber) || src.BankAccountNumber.Length < 4
            ? null
            : src.BankAccountNumber[^4..],
        ExpiryMonth = null,
        ExpiryYear = null,
        DisplayName = displayName ?? src.Alias,
        IsDefault = isDefault || src.IsDefault,
        CreatedAt = src.CreatedAt
    };

    // === OPay API shapes (internal) ===

    private sealed class OPayResponse<T> where T : class
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class OPaySavedBankAccount
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("userId")] public string? UserId { get; set; }
        [JsonPropertyName("bankAccountNumber")] public string? BankAccountNumber { get; set; }
        [JsonPropertyName("bankCode")] public string? BankCode { get; set; }
        [JsonPropertyName("bankName")] public string? BankName { get; set; }
        [JsonPropertyName("alias")] public string? Alias { get; set; }
        [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }

    private sealed class OPaySavedBankAccountList
    {
        [JsonPropertyName("accounts")] public List<OPaySavedBankAccount>? Accounts { get; set; }
    }
}
