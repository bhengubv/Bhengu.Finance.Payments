// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.BricsPay.Currency;

/// <summary>Service for currency exchange operations within BRICS countries.</summary>
public interface ICurrencyExchangeService
{
    /// <summary>Get the current exchange rate between two BRICS currencies.</summary>
    Task<ExchangeRate> GetExchangeRateAsync(BricsCurrency from, BricsCurrency to, CancellationToken ct = default);

    /// <summary>Get all exchange rates for a base currency.</summary>
    Task<IReadOnlyList<ExchangeRate>> GetAllRatesAsync(BricsCurrency baseCurrency, CancellationToken ct = default);

    /// <summary>Convert an amount with fee calculation. Does not lock the rate.</summary>
    Task<ConversionResult> ConvertAsync(decimal amount, BricsCurrency from, BricsCurrency to, CancellationToken ct = default);

    /// <summary>Lock an exchange rate for a transaction (quote valid for limited time).</summary>
    Task<ConversionResult> LockRateAsync(decimal amount, BricsCurrency from, BricsCurrency to, int validMinutes = 15, CancellationToken ct = default);

    /// <summary>Execute a previously-locked conversion by quote ID. Throws if expired or already used.</summary>
    Task<ConversionResult> ExecuteLockedConversionAsync(string quoteId, CancellationToken ct = default);

    /// <summary>Get display metadata for a single currency.</summary>
    CurrencyInfo GetCurrencyInfo(BricsCurrency currency);

    /// <summary>List all supported BRICS currencies.</summary>
    IReadOnlyList<CurrencyInfo> GetAllCurrencies();
}
