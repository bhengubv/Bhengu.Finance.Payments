// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Interswitch.Providers;

/// <summary>
/// Interswitch implementation of <see cref="ISettlementProvider"/> backed by the Interswitch
/// settlement reporting endpoints. Returns daily settlement batches the acquirer has paid out
/// to the merchant's nominated bank account, plus the constituent transactions.
/// </summary>
/// <remarks>
/// Interswitch settlement data is exposed under <c>api/v2/settlements</c> (batch list) and
/// <c>api/v2/settlements/{id}/transactions</c> (line items). All amounts on the wire are in
/// kobo (NGN minor units); the SDK divides by 100 before returning <see cref="Settlement"/>.
/// </remarks>
public sealed class InterswitchSettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private readonly InterswitchHttpClient _http;
    private readonly InterswitchOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Interswitch;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public InterswitchSettlementProvider(
        HttpClient httpClient,
        IOptions<InterswitchOptions> options,
        ILogger<InterswitchSettlementProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientSecret)} is required");

        _http = new InterswitchHttpClient(httpClient, _options, Logger);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var qs = new StringBuilder("api/v2/settlements?pageSize=100");
        qs.Append("&fromDate=").Append(Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        qs.Append("&toDate=").Append(Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        var body = await RunOperationAsync("list_settlements",
            () => _http.SendAsync(HttpMethod.Get, qs.ToString(), null, "ListSettlements", ct), ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<InterswitchSettlementListResponse>(body, InterswitchHttpClient.Json);
        if (resp?.Settlements is null) yield break;

        foreach (var s in resp.Settlements)
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
                var body = await _http.SendAsync(HttpMethod.Get,
                    $"api/v2/settlements/{Uri.EscapeDataString(settlementReference)}",
                    null, "GetSettlement", ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<InterswitchSettlementData>(body, InterswitchHttpClient.Json);
                return resp is null ? null : MapSettlement(resp);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(
        string settlementReference,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        var body = await RunOperationAsync("list_settlement_transactions",
            () => _http.SendAsync(HttpMethod.Get,
                $"api/v2/settlements/{Uri.EscapeDataString(settlementReference)}/transactions?pageSize=100",
                null, "ListSettlementTransactions", ct), ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<InterswitchSettlementTransactionListResponse>(body, InterswitchHttpClient.Json);
        if (resp?.Transactions is null) yield break;

        foreach (var t in resp.Transactions)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(t);
        }
    }

    private static Settlement MapSettlement(InterswitchSettlementData s) => new()
    {
        Reference = s.SettlementId ?? s.BatchId ?? string.Empty,
        NetAmount = s.NetAmount / 100m,
        GrossAmount = s.GrossAmount / 100m,
        Fees = (s.GrossAmount - s.NetAmount) / 100m,
        Currency = s.Currency ?? "NGN",
        SettledAt = s.SettlementDate ?? s.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = s.SettlementAccount,
        TransactionCount = s.TransactionCount
    };

    private static SettlementTransaction MapTransaction(InterswitchSettlementTransactionData t) => new()
    {
        GatewayReference = t.TransactionRef ?? string.Empty,
        Kind = MapKind(t.TransactionType),
        NetAmount = t.NetAmount / 100m,
        GrossAmount = t.GrossAmount / 100m,
        Fee = t.Fee / 100m,
        Currency = t.Currency ?? "NGN",
        CreatedAt = t.TransactionDate ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? rawType) => rawType?.ToLowerInvariant() switch
    {
        "refund" or "reversal" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Charge
    };

    // === Interswitch API response shapes (internal) ===

    private sealed class InterswitchSettlementListResponse
    {
        [JsonPropertyName("settlements")] public List<InterswitchSettlementData>? Settlements { get; set; }
        [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    }

    private sealed class InterswitchSettlementData
    {
        [JsonPropertyName("settlementId")] public string? SettlementId { get; set; }
        [JsonPropertyName("batchId")] public string? BatchId { get; set; }
        [JsonPropertyName("grossAmount")] public long GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public long NetAmount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settlementDate")] public DateTime? SettlementDate { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("settlementAccount")] public string? SettlementAccount { get; set; }
        [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
    }

    private sealed class InterswitchSettlementTransactionListResponse
    {
        [JsonPropertyName("transactions")] public List<InterswitchSettlementTransactionData>? Transactions { get; set; }
        [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    }

    private sealed class InterswitchSettlementTransactionData
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("transactionType")] public string? TransactionType { get; set; }
        [JsonPropertyName("grossAmount")] public long GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public long NetAmount { get; set; }
        [JsonPropertyName("fee")] public long Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("transactionDate")] public DateTime? TransactionDate { get; set; }
    }
}
