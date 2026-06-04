// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Bhengu.Finance.Payments.Fawry.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Fawry.Providers;

/// <summary>
/// Fawry implementation of <see cref="ISettlementProvider"/> backed by Fawry's settlement
/// reporting endpoints. Fawry credits collected funds to the merchant's nominated bank in
/// daily batches; this provider exposes those batches plus per-transaction line items for
/// reconciliation against the merchant's own ledger.
/// </summary>
public sealed class FawrySettlementProvider : ISettlementProvider
{
    private readonly FawryHttpClient _http;
    private readonly FawryOptions _options;
    private readonly ILogger<FawrySettlementProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Fawry;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public FawrySettlementProvider(
        HttpClient httpClient,
        IOptions<FawryOptions> options,
        ILogger<FawrySettlementProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FawryOptions.MerchantCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecurityKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FawryOptions.SecurityKey)} is required");

        _http = new FawryHttpClient(httpClient, _options, _logger);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlements");
        var qs = new StringBuilder("reports/settlements?pageSize=100");
        qs.Append("&merchantCode=").Append(Uri.EscapeDataString(_options.MerchantCode));
        qs.Append("&fromDate=").Append(Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        qs.Append("&toDate=").Append(Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        qs.Append("&signature=").Append(ComputeSignature(_options.MerchantCode, fromUtc, toUtc, _options.SecurityKey));

        var body = await _http.SendAsync(HttpMethod.Get, qs.ToString(), null, "ListSettlements", ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<FawrySettlementListResponse>(body, FawryHttpClient.Json);
        activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

        if (resp?.Settlements is null || resp.Settlements.Count == 0) return Array.Empty<Settlement>();
        var result = new List<Settlement>(resp.Settlements.Count);
        foreach (var s in resp.Settlements) result.Add(MapSettlement(s));
        return result;
    }

    /// <inheritdoc />
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "get_settlement");
        try
        {
            var path = $"reports/settlements/{Uri.EscapeDataString(settlementReference)}?merchantCode={Uri.EscapeDataString(_options.MerchantCode)}";
            var body = await _http.SendAsync(HttpMethod.Get, path, null, "GetSettlement", ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<FawrySettlementData>(body, FawryHttpClient.Json);
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return resp is null || string.IsNullOrEmpty(resp.SettlementId) ? null : MapSettlement(resp);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlement_transactions");
        var path = $"reports/settlements/{Uri.EscapeDataString(settlementReference)}/transactions" +
                   $"?merchantCode={Uri.EscapeDataString(_options.MerchantCode)}&pageSize=100";
        var body = await _http.SendAsync(HttpMethod.Get, path, null, "ListSettlementTransactions", ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<FawrySettlementTransactionListResponse>(body, FawryHttpClient.Json);
        activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

        if (resp?.Transactions is null || resp.Transactions.Count == 0) return Array.Empty<SettlementTransaction>();
        var result = new List<SettlementTransaction>(resp.Transactions.Count);
        foreach (var t in resp.Transactions) result.Add(MapTransaction(t));
        return result;
    }

    private static string ComputeSignature(string merchantCode, DateTime fromUtc, DateTime toUtc, string securityKey)
    {
        var raw = merchantCode
            + fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            + securityKey;
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static Settlement MapSettlement(FawrySettlementData s) => new()
    {
        Reference = s.SettlementId ?? string.Empty,
        NetAmount = s.NetAmount,
        GrossAmount = s.GrossAmount,
        Fees = s.Fees,
        Currency = s.Currency ?? "EGP",
        SettledAt = s.SettlementDate ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccountReference,
        TransactionCount = s.TransactionCount
    };

    private static SettlementTransaction MapTransaction(FawrySettlementTransactionData t) => new()
    {
        GatewayReference = t.FawryRefNumber ?? t.MerchantRefNumber ?? string.Empty,
        Kind = MapKind(t.Type),
        NetAmount = t.NetAmount,
        GrossAmount = t.GrossAmount,
        Fee = t.Fee,
        Currency = t.Currency ?? "EGP",
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

    // === Fawry API shapes (internal) ===

    private sealed class FawrySettlementListResponse
    {
        [JsonPropertyName("settlements")] public List<FawrySettlementData>? Settlements { get; set; }
    }

    private sealed class FawrySettlementData
    {
        [JsonPropertyName("settlementId")] public string? SettlementId { get; set; }
        [JsonPropertyName("grossAmount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fees")] public decimal Fees { get; set; }
        [JsonPropertyName("currencyCode")] public string? Currency { get; set; }
        [JsonPropertyName("settlementDate")] public DateTime? SettlementDate { get; set; }
        [JsonPropertyName("bankAccountReference")] public string? BankAccountReference { get; set; }
        [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
    }

    private sealed class FawrySettlementTransactionListResponse
    {
        [JsonPropertyName("transactions")] public List<FawrySettlementTransactionData>? Transactions { get; set; }
    }

    private sealed class FawrySettlementTransactionData
    {
        [JsonPropertyName("fawryRefNumber")] public string? FawryRefNumber { get; set; }
        [JsonPropertyName("merchantRefNumber")] public string? MerchantRefNumber { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("grossAmount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fee")] public decimal Fee { get; set; }
        [JsonPropertyName("currencyCode")] public string? Currency { get; set; }
        [JsonPropertyName("transactionDate")] public DateTime? TransactionDate { get; set; }
    }
}
