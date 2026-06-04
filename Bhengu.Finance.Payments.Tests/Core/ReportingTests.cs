// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Reporting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Verifies <see cref="PaymentReportingAggregator"/> aggregates per-currency totals across multiple
/// settlement providers, copes with per-provider exceptions (skip-and-continue), handles an empty
/// provider set, and rolls up grand totals across mixed currencies.
/// </summary>
public class ReportingTests
{
    private sealed class StubSettlementProvider : ISettlementProvider
    {
        public string ProviderName { get; }
        private readonly IReadOnlyList<Settlement> _settlements;
        private readonly Exception? _throws;

        public StubSettlementProvider(string name, IReadOnlyList<Settlement>? settlements = null, Exception? throws = null)
        {
            ProviderName = name;
            _settlements = settlements ?? Array.Empty<Settlement>();
            _throws = throws;
        }

#pragma warning disable CS1998 // intentionally async with no awaits — synchronous stub for tests.
        public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_throws is not null)
                throw _throws;
            foreach (var s in _settlements)
                yield return s;
        }
#pragma warning restore CS1998

        public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default) =>
            Task.FromResult<Settlement?>(null);

#pragma warning disable CS1998 // intentionally async with no awaits — synchronous stub for tests.
        public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }

    private static Settlement S(string reference, string currency, decimal net, decimal? gross = null, decimal? fees = null, int txCount = 1) => new()
    {
        Reference = reference,
        NetAmount = net,
        GrossAmount = gross,
        Fees = fees,
        Currency = currency,
        SettledAt = DateTime.UtcNow.AddHours(-1),
        TransactionCount = txCount,
    };

    [Fact]
    public async Task AggregateAsync_EmptyProviders_ReturnsZeroRowsAndZeroGrandTotals()
    {
        var aggregator = new PaymentReportingAggregator([], NullLogger<PaymentReportingAggregator>.Instance);

        var report = await aggregator.AggregateAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        Assert.Empty(report.Rows);
        Assert.Empty(report.GrandTotals);
    }

    [Fact]
    public async Task AggregateAsync_SingleProvider_AggregatesPerCurrencyTotals()
    {
        var provider = new StubSettlementProvider("stripe", [
            S("st_1", "USD", net: 90m, gross: 100m, fees: 10m, txCount: 2),
            S("st_2", "USD", net: 45m, gross: 50m, fees: 5m, txCount: 1),
            S("st_3", "ZAR", net: 1900m, gross: 2000m, fees: 100m, txCount: 4),
        ]);
        var aggregator = new PaymentReportingAggregator([provider], NullLogger<PaymentReportingAggregator>.Instance);

        var report = await aggregator.AggregateAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        var row = Assert.Single(report.Rows);
        Assert.Equal("stripe", row.ProviderName);
        Assert.Equal(3, row.SettlementCount);
        Assert.Equal(7, row.TransactionCount);

        var usd = row.Totals.Single(t => t.Currency == "USD");
        Assert.Equal(135m, usd.NetAmount);
        Assert.Equal(150m, usd.GrossAmount);
        Assert.Equal(15m, usd.Fees);

        var zar = row.Totals.Single(t => t.Currency == "ZAR");
        Assert.Equal(1900m, zar.NetAmount);
    }

    [Fact]
    public async Task AggregateAsync_RollsUpGrandTotalsAcrossProvidersAndMixedCurrencies()
    {
        var stripe = new StubSettlementProvider("stripe", [
            S("st_1", "USD", net: 100m, gross: 110m, fees: 10m),
            S("st_2", "ZAR", net: 500m, gross: 525m, fees: 25m),
        ]);
        var paystack = new StubSettlementProvider("paystack", [
            S("ps_1", "NGN", net: 9500m, gross: 10000m, fees: 500m),
            S("ps_2", "USD", net: 50m, gross: 55m, fees: 5m),
        ]);
        var aggregator = new PaymentReportingAggregator([stripe, paystack], NullLogger<PaymentReportingAggregator>.Instance);

        var report = await aggregator.AggregateAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(3, report.GrandTotals.Count);

        var usdTotal = report.GrandTotals.Single(t => t.Currency == "USD");
        Assert.Equal(150m, usdTotal.NetAmount);
        Assert.Equal(165m, usdTotal.GrossAmount);
        Assert.Equal(15m, usdTotal.Fees);

        var zarTotal = report.GrandTotals.Single(t => t.Currency == "ZAR");
        Assert.Equal(500m, zarTotal.NetAmount);

        var ngnTotal = report.GrandTotals.Single(t => t.Currency == "NGN");
        Assert.Equal(9500m, ngnTotal.NetAmount);
        Assert.Equal(500m, ngnTotal.Fees);
    }

    [Fact]
    public async Task AggregateAsync_FailingProvider_IsSkipped_OthersContinue()
    {
        var stripe = new StubSettlementProvider("stripe", [
            S("st_1", "USD", net: 100m, gross: 110m, fees: 10m),
        ]);
        var failing = new StubSettlementProvider("flutterwave", throws: new InvalidOperationException("provider unavailable"));
        var paystack = new StubSettlementProvider("paystack", [
            S("ps_1", "NGN", net: 5000m, gross: 5250m, fees: 250m),
        ]);

        var aggregator = new PaymentReportingAggregator([stripe, failing, paystack], NullLogger<PaymentReportingAggregator>.Instance);

        var report = await aggregator.AggregateAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        Assert.Equal(3, report.Rows.Count);

        var failingRow = report.Rows.Single(r => r.ProviderName == "flutterwave");
        Assert.Equal(0, failingRow.SettlementCount);
        Assert.Empty(failingRow.Totals);

        // Other providers still produce totals — grand totals should reflect only stripe + paystack.
        Assert.Equal(2, report.GrandTotals.Count);
        Assert.Equal(100m, report.GrandTotals.Single(t => t.Currency == "USD").NetAmount);
        Assert.Equal(5000m, report.GrandTotals.Single(t => t.Currency == "NGN").NetAmount);
    }

    [Fact]
    public async Task AggregateAsync_DefaultsGrossToNet_WhenProviderOmitsGross()
    {
        // Some providers (e.g. low-level mobile-money APIs) only return Net. The aggregator must
        // default Gross to Net so the per-provider Totals row stays internally consistent.
        var provider = new StubSettlementProvider("mpesa", [
            S("mp_1", "KES", net: 999m, gross: null, fees: null),
        ]);
        var aggregator = new PaymentReportingAggregator([provider], NullLogger<PaymentReportingAggregator>.Instance);

        var report = await aggregator.AggregateAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);

        var kes = Assert.Single(report.GrandTotals);
        Assert.Equal("KES", kes.Currency);
        Assert.Equal(999m, kes.NetAmount);
        Assert.Equal(999m, kes.GrossAmount);
        Assert.Equal(0m, kes.Fees);
    }

    [Fact]
    public async Task AggregateAsync_PassesWindowToProvider()
    {
        DateTime? observedFrom = null;
        DateTime? observedTo = null;

        var sentinel = new SpyProvider(name: "stripe",
            onList: (from, to) => { observedFrom = from; observedTo = to; });

        var aggregator = new PaymentReportingAggregator([sentinel], NullLogger<PaymentReportingAggregator>.Instance);
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 5, 31, 23, 59, 59, DateTimeKind.Utc);

        _ = await aggregator.AggregateAsync(from, to);

        Assert.Equal(from, observedFrom);
        Assert.Equal(to, observedTo);
    }

    private sealed class SpyProvider : ISettlementProvider
    {
        private readonly Action<DateTime, DateTime> _onList;
        public string ProviderName { get; }

        public SpyProvider(string name, Action<DateTime, DateTime> onList)
        {
            ProviderName = name;
            _onList = onList;
        }

#pragma warning disable CS1998 // intentionally async with no awaits — synchronous spy for tests.
        public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            _onList(fromUtc, toUtc);
            yield break;
        }
#pragma warning restore CS1998

        public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default) =>
            Task.FromResult<Settlement?>(null);

#pragma warning disable CS1998 // intentionally async with no awaits — synchronous spy for tests.
        public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield break;
        }
#pragma warning restore CS1998
    }
}
