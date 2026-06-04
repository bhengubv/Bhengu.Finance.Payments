// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ExpressPay.Providers;

/// <summary>
/// ExpressPay settlement provider — wraps the <c>settlements.php</c> reconciliation feed.
/// <para>
/// ExpressPay exposes settlements via a date-range form post and constituent transactions via the
/// settlement-id form post. Both endpoints accept the standard merchant-id / api-key form fields.
/// </para>
/// </summary>
public sealed class ExpressPaySettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly ExpressPayOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.ExpressPay;

    /// <summary>Construct the ExpressPay settlement provider. Designed to be registered via DI.</summary>
    public ExpressPaySettlementProvider(
        HttpClient httpClient,
        IOptions<ExpressPayOptions> options,
        ILogger<ExpressPaySettlementProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://sandbox.expresspaygh.com/api/"
                : _options.BaseUrl ?? "https://expresspay.com.gh/api/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<ExpressPaySettlementData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list"))
        {
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["merchant-id"] = _options.MerchantId,
                    ["api-key"] = _options.ApiKey,
                    ["from"] = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["to"] = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };

                var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, Logger, HttpMethod.Post, "settlements.php", form, ct, "ListSettlements").ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<ExpressPaySettlementListResponse>(responseBody);
                items = envelope?.Settlements;

                Logger.LogInformation("ExpressPay settlements listed: {Count} between {From:O} and {To:O}", items?.Count ?? 0, fromUtc, toUtc);
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            }
            catch (Exception ex)
            {
                activity.SetOutcome(ClassifyOutcome(ex));
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var d in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToSettlement(d);
        }
    }

    /// <inheritdoc/>
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return RunOperationAsync<Settlement?>("get_settlement", async () =>
        {
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["merchant-id"] = _options.MerchantId,
                    ["api-key"] = _options.ApiKey,
                    ["settlement-id"] = settlementReference
                };

                var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, Logger, HttpMethod.Post, "settlement.php", form, ct, "GetSettlement").ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<ExpressPaySettlementSingleResponse>(responseBody);
                return envelope?.Settlement is null ? null : ToSettlement(envelope.Settlement);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        List<ExpressPayTransactionData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list_transactions"))
        {
            try
            {
                var form = new Dictionary<string, string>
                {
                    ["merchant-id"] = _options.MerchantId,
                    ["api-key"] = _options.ApiKey,
                    ["settlement-id"] = settlementReference
                };

                var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, Logger, HttpMethod.Post, "settlement_transactions.php", form, ct, "ListSettlementTransactions").ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<ExpressPayTransactionListResponse>(responseBody);
                items = envelope?.Transactions;
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            }
            catch (Exception ex)
            {
                activity.SetOutcome(ClassifyOutcome(ex));
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var d in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToTransaction(d);
        }
    }

    private Settlement ToSettlement(ExpressPaySettlementData d) => new()
    {
        Reference = d.SettlementId ?? string.Empty,
        NetAmount = d.NetAmount,
        GrossAmount = d.GrossAmount == 0 ? null : d.GrossAmount,
        Fees = d.Fees == 0 ? null : d.Fees,
        Currency = d.Currency ?? _options.Currency,
        SettledAt = d.SettledAt ?? d.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = d.BankAccount,
        TransactionCount = d.TransactionCount
    };

    private SettlementTransaction ToTransaction(ExpressPayTransactionData d) => new()
    {
        GatewayReference = d.Token ?? d.OrderId ?? string.Empty,
        Kind = MapKind(d.Type, d.Amount),
        NetAmount = d.NetAmount == 0 ? d.Amount : d.NetAmount,
        GrossAmount = d.Amount == 0 ? null : d.Amount,
        Fee = d.Fee == 0 ? null : d.Fee,
        Currency = d.Currency ?? _options.Currency,
        CreatedAt = d.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? type, decimal amount) =>
        type?.ToLowerInvariant() switch
        {
            "refund" or "refunded" => SettlementTransactionKind.Refund,
            "chargeback" => SettlementTransactionKind.Chargeback,
            "fee" => SettlementTransactionKind.Fee,
            "adjustment" => SettlementTransactionKind.Adjustment,
            _ when amount < 0 => SettlementTransactionKind.Refund,
            _ => SettlementTransactionKind.Charge
        };

    private static string ClassifyOutcome(Exception ex) => ex switch
    {
        PaymentDeclinedException => BhenguPaymentDiagnostics.Outcomes.Declined,
        ProviderRateLimitException => BhenguPaymentDiagnostics.Outcomes.RateLimited,
        ProviderUnavailableException => BhenguPaymentDiagnostics.Outcomes.Unavailable,
        _ => BhenguPaymentDiagnostics.Outcomes.Error
    };

    // === ExpressPay API shapes (internal) ===

    private sealed class ExpressPaySettlementListResponse
    {
        [JsonPropertyName("settlements")] public List<ExpressPaySettlementData>? Settlements { get; set; }
    }

    private sealed class ExpressPaySettlementSingleResponse
    {
        [JsonPropertyName("settlement")] public ExpressPaySettlementData? Settlement { get; set; }
    }

    private sealed class ExpressPaySettlementData
    {
        [JsonPropertyName("settlement-id")] public string? SettlementId { get; set; }
        [JsonPropertyName("net-amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("gross-amount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("fees")] public decimal Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled-at")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("created-at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("bank-account")] public string? BankAccount { get; set; }
        [JsonPropertyName("transaction-count")] public int TransactionCount { get; set; }
    }

    private sealed class ExpressPayTransactionListResponse
    {
        [JsonPropertyName("transactions")] public List<ExpressPayTransactionData>? Transactions { get; set; }
    }

    private sealed class ExpressPayTransactionData
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("order-id")] public string? OrderId { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("net-amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fee")] public decimal Fee { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("created-at")] public DateTime? CreatedAt { get; set; }
    }
}
