// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OPay.Providers;

/// <summary>
/// READ-side OPay tokenisation provider — saved-bank-account fetch / list / delete. The
/// WRITE-side counterpart that registers raw bank-account credentials lives in
/// <see cref="OPayRawCardTokenisationProvider"/>.
/// </summary>
public sealed class OPayTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    internal readonly OPayHttpClient Http;
    internal readonly OPayOptions Options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.OPay;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public OPayTokenisationProvider(
        HttpClient httpClient,
        IOptions<OPayOptions> options,
        ILogger<OPayTokenisationProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        Options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(Options.PublicKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.PublicKey)} is required");
        if (string.IsNullOrWhiteSpace(Options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.SecretKey)} is required");
        if (string.IsNullOrWhiteSpace(Options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.MerchantId)} is required");

        Http = new OPayHttpClient(httpClient, Options, Logger);
    }

    /// <inheritdoc />
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync<PaymentMethod?>("get_payment_method", async () =>
        {
            try
            {
                var body = new { publicKey = Options.PublicKey, sn = Options.MerchantId, token };
                var json = await Http.SendAsync(HttpMethod.Post,
                    "api/v1/international/cashier/savedBankAccount/query", body, "GetPaymentMethod", ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<OPayResponseEnvelope<OPaySavedBankAccount>>(json, OPayHttpClient.Json);
                if (resp?.Data?.Token is null) return null;
                return Map(resp.Data, resp.Data.UserId, null, resp.Data.IsDefault);
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
        var body = new { publicKey = Options.PublicKey, sn = Options.MerchantId, userId = customerId };
        var json = await RunOperationAsync("list_payment_methods",
            () => Http.SendAsync(HttpMethod.Post,
                "api/v1/international/cashier/savedBankAccount/list", body, "ListPaymentMethods", ct), ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponseEnvelope<OPaySavedBankAccountList>>(json, OPayHttpClient.Json);
        if (resp?.Data?.Accounts is null) yield break;
        foreach (var a in resp.Data.Accounts)
        {
            ct.ThrowIfCancellationRequested();
            yield return Map(a, customerId, null, a.IsDefault);
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
                var body = new { publicKey = Options.PublicKey, sn = Options.MerchantId, token };
                var json = await Http.SendAsync(HttpMethod.Post,
                    "api/v1/international/cashier/savedBankAccount/remove", body, "DeletePaymentMethod", ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<OPayResponseEnvelope<object>>(json, OPayHttpClient.Json);
                return string.Equals(resp?.Code, "00000", StringComparison.Ordinal);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return false;
            }
        }, ct);
    }

    internal static PaymentMethod Map(OPaySavedBankAccount src, string? customerId, string? displayName, bool isDefault) => new()
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

    internal sealed class OPayResponseEnvelope<T> where T : class
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    internal sealed class OPaySavedBankAccount
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

    internal sealed class OPaySavedBankAccountList
    {
        [JsonPropertyName("accounts")] public List<OPaySavedBankAccount>? Accounts { get; set; }
    }
}
