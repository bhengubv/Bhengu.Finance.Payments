// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Reporting;

/// <summary>
/// Cross-provider reporting facade. Aggregates settlement, charge volume, refund volume and fee
/// totals across every registered <see cref="Interfaces.ISettlementProvider"/> so consumers get
/// one ledger view instead of querying each provider's dashboard separately.
/// </summary>
public interface IPaymentReportingAggregator
{
    /// <summary>
    /// Aggregate settlement-level totals across all providers that support settlement sync.
    /// </summary>
    Task<AggregatedReport> AggregateAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}

/// <summary>
/// One row per provider plus a grand-total roll-up.
/// </summary>
public sealed record AggregatedReport
{
    /// <summary>UTC window the report covers.</summary>
    public required DateTime FromUtc { get; init; }

    /// <summary>UTC window the report covers.</summary>
    public required DateTime ToUtc { get; init; }

    /// <summary>One row per provider.</summary>
    public required IReadOnlyList<ProviderReportRow> Rows { get; init; }

    /// <summary>Grand-total roll-up across all rows (treating mixed currencies as separate columns is the consumer's job).</summary>
    public required IReadOnlyList<CurrencyTotal> GrandTotals { get; init; }
}

/// <summary>Per-provider report row.</summary>
public sealed record ProviderReportRow
{
    /// <summary>Provider name.</summary>
    public required string ProviderName { get; init; }

    /// <summary>Number of settlements in the window.</summary>
    public int SettlementCount { get; init; }

    /// <summary>Number of constituent transactions across those settlements.</summary>
    public int TransactionCount { get; init; }

    /// <summary>Currency totals for this provider.</summary>
    public required IReadOnlyList<CurrencyTotal> Totals { get; init; }
}

/// <summary>One currency's totals.</summary>
public sealed record CurrencyTotal
{
    /// <summary>ISO 4217 currency.</summary>
    public required string Currency { get; init; }

    /// <summary>Gross before fees.</summary>
    public decimal GrossAmount { get; init; }

    /// <summary>Net after fees.</summary>
    public decimal NetAmount { get; init; }

    /// <summary>Total provider fees.</summary>
    public decimal Fees { get; init; }
}
