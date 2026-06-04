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
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Moniepoint.Providers;

/// <summary>
/// Moniepoint implementation of <see cref="ISettlementProvider"/> backed by Moniepoint's
/// settlement reporting endpoints — daily batches the acquirer credits to the merchant's
/// Moniepoint business account.
/// </summary>
public sealed class MoniepointSettlementProvider : ISettlementProvider
{
    private readonly MoniepointHttpClient _http;
    private readonly MoniepointOptions _options;
    private readonly ILogger<MoniepointSettlementProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Moniepoint;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public MoniepointSettlementProvider(
        HttpClient httpClient,
        IOptions<MoniepointOptions> options,
        ILogger<MoniepointSettlementProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MoniepointOptions.ApiKey)} is required");

        _http = new MoniepointHttpClient(httpClient, _options, _logger);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlements");
        var qs = new StringBuilder("api/v1/settlements?pageSize=100");
        qs.Append("&from=").Append(Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        qs.Append("&to=").Append(Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));

        var body = await _http.SendAsync(HttpMethod.Get, qs.ToString(), null, "ListSettlements", ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointSettlementListResponse>(body, MoniepointHttpClient.Json);
        activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

        if (resp?.Data is null || resp.Data.Count == 0) return Array.Empty<Settlement>();
        var result = new List<Settlement>(resp.Data.Count);
        foreach (var s in resp.Data) result.Add(MapSettlement(s));
        return result;
    }

    /// <inheritdoc />
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "get_settlement");
        try
        {
            var body = await _http.SendAsync(HttpMethod.Get,
                $"api/v1/settlements/{Uri.EscapeDataString(settlementReference)}", null, "GetSettlement", ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<MoniepointSettlementResponse>(body, MoniepointHttpClient.Json);
            activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return resp?.Data is null ? null : MapSettlement(resp.Data);
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
        var body = await _http.SendAsync(HttpMethod.Get,
            $"api/v1/settlements/{Uri.EscapeDataString(settlementReference)}/transactions?pageSize=100",
            null, "ListSettlementTransactions", ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointSettlementTransactionListResponse>(body, MoniepointHttpClient.Json);
        activity?.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

        if (resp?.Data is null || resp.Data.Count == 0) return Array.Empty<SettlementTransaction>();
        var result = new List<SettlementTransaction>(resp.Data.Count);
        foreach (var t in resp.Data) result.Add(MapTransaction(t));
        return result;
    }

    private static Settlement MapSettlement(MoniepointSettlementData s) => new()
    {
        Reference = s.Reference ?? s.Id ?? string.Empty,
        NetAmount = s.NetAmount,
        GrossAmount = s.GrossAmount,
        Fees = s.Fees,
        Currency = s.Currency ?? "NGN",
        SettledAt = s.SettledAt ?? s.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccount,
        TransactionCount = s.TransactionCount
    };

    private static SettlementTransaction MapTransaction(MoniepointSettlementTransactionData t) => new()
    {
        GatewayReference = t.Reference ?? string.Empty,
        Kind = MapKind(t.Type),
        NetAmount = t.NetAmount,
        GrossAmount = t.GrossAmount,
        Fee = t.Fee,
        Currency = t.Currency ?? "NGN",
        CreatedAt = t.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "refund" or "reversal" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" or "service_fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Charge
    };

    // === Moniepoint API shapes (internal) ===

    private sealed class MoniepointSettlementListResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public List<MoniepointSettlementData>? Data { get; set; }
    }

    private sealed class MoniepointSettlementResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public MoniepointSettlementData? Data { get; set; }
    }

    private sealed class MoniepointSettlementData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("netAmount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("grossAmount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("fees")] public decimal Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settledAt")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("bankAccount")] public string? BankAccount { get; set; }
        [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
    }

    private sealed class MoniepointSettlementTransactionListResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public List<MoniepointSettlementTransactionData>? Data { get; set; }
    }

    private sealed class MoniepointSettlementTransactionData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("netAmount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("grossAmount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("fee")] public decimal Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}
