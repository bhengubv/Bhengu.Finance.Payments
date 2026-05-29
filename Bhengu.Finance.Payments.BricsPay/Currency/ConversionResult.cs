// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.BricsPay.Currency;

/// <summary>The result of converting an amount between two BRICS currencies, including any fees and quote lock.</summary>
public sealed record ConversionResult
{
    public decimal OriginalAmount { get; init; }
    public BricsCurrency OriginalCurrency { get; init; }
    public decimal ConvertedAmount { get; init; }
    public BricsCurrency TargetCurrency { get; init; }
    public decimal ExchangeRate { get; init; }
    public decimal Fee { get; init; }
    public decimal FinalAmount { get; init; }
    public DateTime QuoteExpiry { get; init; }
    public string QuoteId { get; init; } = string.Empty;
}
