// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paystack.Providers;

/// <summary>
/// Paystack implementation of <see cref="IMarketplaceProvider"/> backed by Paystack's
/// <c>/subaccount</c> and <c>/split</c> endpoints, plus the <c>subaccount</c> + <c>split_code</c>
/// parameters on charge requests for marketplace flows.
/// </summary>
/// <remarks>
/// Paystack onboarding for sub-accounts is API-only — there is no hosted KYC flow as of writing,
/// so <see cref="SubAccount.OnboardingUrl"/> is always null and sub-accounts are immediately
/// active. <see cref="SubAccountRequest.SettlementAccountToken"/> must be a Paystack-resolved
/// account number (use Paystack's bank resolution endpoint first).
/// </remarks>
public sealed class PaystackMarketplaceProvider : BhenguProviderBase, IMarketplaceProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;
    private readonly PaystackIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paystack;

    /// <summary>Construct a marketplace provider. Designed to be registered via DI.</summary>
    public PaystackMarketplaceProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackMarketplaceProvider> logger,
        PaystackIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        PaystackHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_sub_account",
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreateSubAccountCoreAsync(request, ct)),
            ct);
    }

    private async Task<SubAccount> CreateSubAccountCoreAsync(SubAccountRequest request, CancellationToken ct)
    {
        var settlementBank = request.Metadata?.GetValueOrDefault("settlement_bank")
            ?? throw new BhenguPaymentException(ProviderName,
                "Paystack requires 'settlement_bank' (bank code) on SubAccountRequest.Metadata to create a sub-account.",
                "missing_settlement_bank");

        var percentageCharge = decimal.TryParse(
            request.Metadata?.GetValueOrDefault("percentage_charge"),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var pc) ? pc : 0m;

        var body = new
        {
            business_name = request.BusinessName,
            settlement_bank = settlementBank,
            account_number = request.SettlementAccountToken
                ?? throw new BhenguPaymentException(ProviderName,
                    "Paystack requires SettlementAccountToken (NUBAN / bank account number) on SubAccountRequest.",
                    "missing_account_number"),
            percentage_charge = percentageCharge,
            primary_contact_email = request.ContactEmail,
            primary_contact_name = request.Metadata?.GetValueOrDefault("primary_contact_name"),
            primary_contact_phone = request.Metadata?.GetValueOrDefault("primary_contact_phone"),
            description = request.Metadata?.GetValueOrDefault("description")
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "subaccount", body, "CreateSubAccount", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackSubAccountResponse>(responseBody, PaystackHttpClient.Json);
        var data = response?.Data
            ?? throw new BhenguPaymentException(ProviderName, "Paystack subaccount create returned no data", "no_subaccount_data");

        return MapSubAccount(data, fallback: request);
    }

    /// <inheritdoc/>
    public Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subAccountReference);
        return RunOperationAsync("get_sub_account", () => GetSubAccountCoreAsync(subAccountReference, ct), ct);
    }

    private async Task<SubAccount?> GetSubAccountCoreAsync(string subAccountReference, CancellationToken ct)
    {
        try
        {
            var responseBody = await PaystackHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, $"subaccount/{Uri.EscapeDataString(subAccountReference)}", null, "GetSubAccount", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaystackSubAccountResponse>(responseBody, PaystackHttpClient.Json);
            return response?.Data is { } data ? MapSubAccount(data, null) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SubAccount> ListSubAccountsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Get, "subaccount?perPage=100", null, "ListSubAccounts", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackSubAccountListResponse>(responseBody, PaystackHttpClient.Json);
        if (response?.Data is null) yield break;

        foreach (var data in response.Data)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSubAccount(data, null);
        }
    }

    /// <inheritdoc/>
    public Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_split",
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreateSplitCoreAsync(request, ct)),
            ct);
    }

    private async Task<SplitDefinition> CreateSplitCoreAsync(SplitDefinitionRequest request, CancellationToken ct)
    {
        if (request.Rules.Count == 0)
            throw new BhenguPaymentException(ProviderName, "Split definition must contain at least one rule.", "empty_split");

        var firstShareType = request.Rules[0].ShareType;
        foreach (var r in request.Rules)
            if (r.ShareType != firstShareType)
                throw new BhenguPaymentException(ProviderName, "Paystack splits cannot mix FixedAmount and Percentage rules.", "mixed_share_types");

        var body = new
        {
            name = request.Name,
            type = firstShareType == SplitShareType.Percentage ? "percentage" : "flat",
            currency = request.Currency.ToUpperInvariant(),
            subaccounts = request.Rules.Select(r => new
            {
                subaccount = r.SubAccountReference,
                share = r.ShareType == SplitShareType.Percentage
                    ? r.Percentage ?? 0m
                    : (r.Amount ?? 0m) * 100m
            }).ToArray(),
            bearer_type = "all-proportional"
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "split", body, "CreateSplit", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackSplitResponse>(responseBody, PaystackHttpClient.Json);
        var data = response?.Data
            ?? throw new BhenguPaymentException(ProviderName, "Paystack split create returned no data", "no_split_data");

        return MapSplit(data, fallback: request);
    }

    /// <inheritdoc/>
    public Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(splitReference);
        return RunOperationAsync("get_split", () => GetSplitCoreAsync(splitReference, ct), ct);
    }

    private async Task<SplitDefinition?> GetSplitCoreAsync(string splitReference, CancellationToken ct)
    {
        try
        {
            var responseBody = await PaystackHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, $"split/{Uri.EscapeDataString(splitReference)}", null, "GetSplit", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaystackSplitResponse>(responseBody, PaystackHttpClient.Json);
            return response?.Data is { } data ? MapSplit(data, null) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Payment.Currency, () => ChargeWithSplitCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ChargeWithSplitCoreAsync(ChargeWithSplitRequest request, CancellationToken ct)
    {
        if (request.SplitReference is null && (request.InlineRules is null || request.InlineRules.Count == 0))
            throw new BhenguPaymentException(ProviderName, "ChargeWithSplitRequest requires SplitReference or InlineRules.", "missing_split");

        var amountInSmallestUnit = (long)(request.Payment.Amount * 100m);
        var email = request.Payment.Metadata?.GetValueOrDefault("email") ?? _options.DefaultEmail
            ?? throw new PaymentDeclinedException(ProviderName, "missing_email",
                "Paystack requires an 'email' in PaymentRequest.Metadata or PaystackOptions.DefaultEmail.");

        var subaccountFromInline = request.InlineRules?.FirstOrDefault()?.SubAccountReference;
        var subaccountFromMetadata = request.Payment.Metadata?.GetValueOrDefault("subaccount");

        var body = new
        {
            authorization_code = request.Payment.PaymentMethodToken,
            email,
            amount = amountInSmallestUnit,
            currency = request.Payment.Currency.ToUpperInvariant(),
            split_code = request.SplitReference,
            subaccount = subaccountFromInline ?? subaccountFromMetadata,
            reference = $"paystack-split-{Guid.NewGuid():N}"
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "transaction/charge_authorization", body, "ChargeWithSplit", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackChargeResponse>(responseBody, PaystackHttpClient.Json);

        return new PaymentResponse
        {
            GatewayReference = response?.Data?.Reference ?? string.Empty,
            Status = MapPaymentStatus(response?.Data?.Status),
            Amount = request.Payment.Amount,
            Currency = request.Payment.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = response?.Message
        };
    }

    private static PaymentStatus MapPaymentStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "completed" => PaymentStatus.Completed,
        "failed" or "abandoned" or "reversed" => PaymentStatus.Failed,
        "pending" or "ongoing" or "processing" => PaymentStatus.Pending,
        _ => PaymentStatus.Pending
    };

    private static SubAccount MapSubAccount(PaystackSubAccountData data, SubAccountRequest? fallback) => new()
    {
        Reference = data.SubAccountCode ?? string.Empty,
        BusinessName = data.BusinessName ?? fallback?.BusinessName ?? string.Empty,
        ContactEmail = data.PrimaryContactEmail ?? fallback?.ContactEmail,
        SettlementAccountToken = data.AccountNumber ?? fallback?.SettlementAccountToken,
        IsActive = data.Active ?? true,
        OnboardingUrl = null
    };

    private static SplitDefinition MapSplit(PaystackSplitData data, SplitDefinitionRequest? fallback)
    {
        var rules = new List<SplitRule>();
        if (data.Subaccounts is not null)
        {
            var isPercentage = string.Equals(data.Type, "percentage", StringComparison.OrdinalIgnoreCase);
            foreach (var sa in data.Subaccounts)
            {
                rules.Add(new SplitRule
                {
                    SubAccountReference = sa.SubAccount?.SubAccountCode ?? sa.Subaccount ?? string.Empty,
                    ShareType = isPercentage ? SplitShareType.Percentage : SplitShareType.FixedAmount,
                    Amount = isPercentage ? null : sa.Share / 100m,
                    Percentage = isPercentage ? sa.Share : null,
                    BearsTransactionFee = string.Equals(data.BearerType, "subaccount", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        else if (fallback is not null)
        {
            rules.AddRange(fallback.Rules);
        }

        return new SplitDefinition
        {
            Reference = data.SplitCode ?? string.Empty,
            Name = data.Name ?? fallback?.Name ?? string.Empty,
            Currency = data.Currency ?? fallback?.Currency ?? "NGN",
            Rules = rules
        };
    }

    // === Paystack API shapes (internal) ===

    private sealed class PaystackSubAccountResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public PaystackSubAccountData? Data { get; set; }
    }

    private sealed class PaystackSubAccountListResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public List<PaystackSubAccountData>? Data { get; set; }
    }

    private sealed class PaystackSubAccountData
    {
        [JsonPropertyName("subaccount_code")] public string? SubAccountCode { get; set; }
        [JsonPropertyName("business_name")] public string? BusinessName { get; set; }
        [JsonPropertyName("primary_contact_email")] public string? PrimaryContactEmail { get; set; }
        [JsonPropertyName("account_number")] public string? AccountNumber { get; set; }
        [JsonPropertyName("settlement_bank")] public string? SettlementBank { get; set; }
        [JsonPropertyName("active")] public bool? Active { get; set; }
    }

    private sealed class PaystackSplitResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public PaystackSplitData? Data { get; set; }
    }

    private sealed class PaystackSplitData
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("split_code")] public string? SplitCode { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("bearer_type")] public string? BearerType { get; set; }
        [JsonPropertyName("subaccounts")] public List<PaystackSplitSubAccount>? Subaccounts { get; set; }
    }

    private sealed class PaystackSplitSubAccount
    {
        [JsonPropertyName("subaccount")] public string? Subaccount { get; set; }
        [JsonPropertyName("subaccount_data")] public PaystackSubAccountData? SubAccount { get; set; }
        [JsonPropertyName("share")] public decimal Share { get; set; }
    }

    private sealed class PaystackChargeResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackChargeData? Data { get; set; }
    }

    private sealed class PaystackChargeData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
