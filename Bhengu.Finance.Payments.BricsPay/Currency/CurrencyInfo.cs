// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.BricsPay.Currency;

/// <summary>Display metadata for a BRICS currency.</summary>
public sealed record CurrencyInfo
{
    public BricsCurrency Code { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string CountryCode { get; init; } = string.Empty;
    public string FlagEmoji { get; init; } = string.Empty;
    public int DecimalPlaces { get; init; } = 2;
}
