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
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Remita.Providers;

/// <summary>
/// Remita implementation of <see cref="ISettlementProvider"/> backed by the Remita e-collection
/// settlement reporting endpoint. Returns daily batches the platform credited to the merchant's
/// nominated collection account, with line-item drill-down.
/// </summary>
public sealed class RemitaSettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private const string SettlementListPath = "remita/exapp/api/v1/send/api/echannelsvc/echannel/settlement/list";
    private const string SettlementDetailPath = "remita/exapp/api/v1/send/api/echannelsvc/echannel/settlement/detail";
    private const string SettlementTransactionsPath = "remita/exapp/api/v1/send/api/echannelsvc/echannel/settlement/transactions";

    private readonly RemitaHttpClient _http;
    private readonly RemitaOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Remita;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public RemitaSettlementProvider(
        HttpClient httpClient,
        IOptions<RemitaOptions> options,
        ILogger<RemitaSettlementProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.ApiKey)} is required");

        _http = new RemitaHttpClient(httpClient, _options, Logger);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<RemitaSettlementData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlements"))
        {
            var fromDate = fromUtc.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var toDate = toUtc.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var hash = RemitaHttpClient.Sha512Hex(_options.MerchantId + fromDate + toDate + _options.ApiKey);

            var body = new
            {
                merchantId = _options.MerchantId,
                fromDate,
                toDate,
                hash
            };

            var json = await _http.SendAsync(HttpMethod.Post, SettlementListPath, body, "ListSettlements", hash, ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<RemitaSettlementListResponse>(json, RemitaHttpClient.Json);
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            items = resp?.Settlements;
        }

        if (items is null) yield break;
        foreach (var s in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSettlement(s);
        }
    }

    /// <inheritdoc />
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return RunOperationAsync<Settlement?>("get_settlement", async () =>
        {
            try
            {
                var hash = RemitaHttpClient.Sha512Hex(_options.MerchantId + settlementReference + _options.ApiKey);
                var body = new
                {
                    merchantId = _options.MerchantId,
                    settlementId = settlementReference,
                    hash
                };
                var json = await _http.SendAsync(HttpMethod.Post, SettlementDetailPath, body, "GetSettlement", hash, ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<RemitaSettlementData>(json, RemitaHttpClient.Json);
                return resp is null || string.IsNullOrEmpty(resp.SettlementId) ? null : MapSettlement(resp);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        List<RemitaSettlementTransactionData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlement_transactions"))
        {
            var hash = RemitaHttpClient.Sha512Hex(_options.MerchantId + settlementReference + _options.ApiKey);
            var body = new
            {
                merchantId = _options.MerchantId,
                settlementId = settlementReference,
                hash
            };
            var json = await _http.SendAsync(HttpMethod.Post, SettlementTransactionsPath, body, "ListSettlementTransactions", hash, ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<RemitaSettlementTransactionListResponse>(json, RemitaHttpClient.Json);
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            items = resp?.Transactions;
        }

        if (items is null) yield break;
        foreach (var t in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(t);
        }
    }

    private static Settlement MapSettlement(RemitaSettlementData s) => new()
    {
        Reference = s.SettlementId ?? string.Empty,
        NetAmount = s.NetAmount,
        GrossAmount = s.GrossAmount,
        Fees = s.Fees,
        Currency = s.Currency ?? "NGN",
        SettledAt = s.SettlementDate ?? DateTime.UtcNow,
        BankAccountReference = s.SettlementAccount,
        TransactionCount = s.TransactionCount
    };

    private static SettlementTransaction MapTransaction(RemitaSettlementTransactionData t) => new()
    {
        GatewayReference = t.Rrr ?? t.TransRef ?? string.Empty,
        Kind = MapKind(t.Type),
        NetAmount = t.NetAmount,
        GrossAmount = t.GrossAmount,
        Fee = t.Fee,
        Currency = t.Currency ?? "NGN",
        CreatedAt = t.TransactionDate ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "refund" or "reversal" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" or "service_fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Charge
    };

    // === Remita API shapes (internal) ===

    private sealed class RemitaSettlementListResponse
    {
        [JsonPropertyName("settlements")] public List<RemitaSettlementData>? Settlements { get; set; }
    }

    private sealed class RemitaSettlementData
    {
        [JsonPropertyName("settlementId")] public string? SettlementId { get; set; }
        [JsonPropertyName("grossAmount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fees")] public decimal Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settlementDate")] public DateTime? SettlementDate { get; set; }
        [JsonPropertyName("settlementAccount")] public string? SettlementAccount { get; set; }
        [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
    }

    private sealed class RemitaSettlementTransactionListResponse
    {
        [JsonPropertyName("transactions")] public List<RemitaSettlementTransactionData>? Transactions { get; set; }
    }

    private sealed class RemitaSettlementTransactionData
    {
        [JsonPropertyName("rrr")] public string? Rrr { get; set; }
        [JsonPropertyName("transRef")] public string? TransRef { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("grossAmount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fee")] public decimal Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("transactionDate")] public DateTime? TransactionDate { get; set; }
    }
}
