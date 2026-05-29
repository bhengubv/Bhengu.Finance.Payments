// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.BricsPay.Currency;

/// <summary>An exchange rate between two BRICS currencies at a point in time.</summary>
public sealed record ExchangeRate
{
    public BricsCurrency FromCurrency { get; init; }
    public BricsCurrency ToCurrency { get; init; }
    public decimal Rate { get; init; }
    public DateTime RateDate { get; init; }
    public string Source { get; init; } = "BRICS_EXCHANGE";
}
