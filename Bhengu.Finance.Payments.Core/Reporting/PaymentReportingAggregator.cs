// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Core.Reporting;

/// <summary>
/// Default <see cref="IPaymentReportingAggregator"/>. Iterates every registered
/// <see cref="ISettlementProvider"/>, sums per-currency, and produces a single roll-up.
/// </summary>
public sealed class PaymentReportingAggregator : IPaymentReportingAggregator
{
    private readonly IEnumerable<ISettlementProvider> _settlementProviders;
    private readonly ILogger<PaymentReportingAggregator> _logger;

    /// <summary>Construct the aggregator.</summary>
    public PaymentReportingAggregator(
        IEnumerable<ISettlementProvider> settlementProviders,
        ILogger<PaymentReportingAggregator> logger)
    {
        _settlementProviders = settlementProviders ?? throw new ArgumentNullException(nameof(settlementProviders));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<AggregatedReport> AggregateAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var rows = new List<ProviderReportRow>();
        var grandTotalsByCurrency = new Dictionary<string, (decimal Gross, decimal Net, decimal Fees)>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _settlementProviders)
        {
            try
            {
                var perCurrency = new Dictionary<string, (decimal Gross, decimal Net, decimal Fees)>(StringComparer.OrdinalIgnoreCase);
                var txCount = 0;
                var settlementCount = 0;

                await foreach (var s in provider.ListSettlementsAsync(fromUtc, toUtc, ct).ConfigureAwait(false))
                {
                    settlementCount++;
                    var bucket = perCurrency.GetValueOrDefault(s.Currency);
                    bucket.Net += s.NetAmount;
                    bucket.Gross += s.GrossAmount ?? s.NetAmount;
                    bucket.Fees += s.Fees ?? 0m;
                    perCurrency[s.Currency] = bucket;
                    txCount += s.TransactionCount;

                    var grand = grandTotalsByCurrency.GetValueOrDefault(s.Currency);
                    grand.Net += s.NetAmount;
                    grand.Gross += s.GrossAmount ?? s.NetAmount;
                    grand.Fees += s.Fees ?? 0m;
                    grandTotalsByCurrency[s.Currency] = grand;
                }

                rows.Add(new ProviderReportRow
                {
                    ProviderName = provider.ProviderName,
                    SettlementCount = settlementCount,
                    TransactionCount = txCount,
                    Totals = perCurrency.Select(kv => new CurrencyTotal
                    {
                        Currency = kv.Key,
                        GrossAmount = kv.Value.Gross,
                        NetAmount = kv.Value.Net,
                        Fees = kv.Value.Fees
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reporting: settlement query failed for {Provider}; skipping in aggregate", provider.ProviderName);
                rows.Add(new ProviderReportRow
                {
                    ProviderName = provider.ProviderName,
                    SettlementCount = 0,
                    TransactionCount = 0,
                    Totals = Array.Empty<CurrencyTotal>()
                });
            }
        }

        return new AggregatedReport
        {
            FromUtc = fromUtc,
            ToUtc = toUtc,
            Rows = rows,
            GrandTotals = grandTotalsByCurrency.Select(kv => new CurrencyTotal
            {
                Currency = kv.Key,
                GrossAmount = kv.Value.Gross,
                NetAmount = kv.Value.Net,
                Fees = kv.Value.Fees
            }).ToList()
        };
    }
}
