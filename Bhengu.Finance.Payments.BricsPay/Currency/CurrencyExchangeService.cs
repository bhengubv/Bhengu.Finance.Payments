// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.BricsPay.Currency;

/// <summary>
/// In-memory currency exchange service for BRICS countries.
/// Fetches live rates from frankfurter.app where available, falls back to a static baseline.
/// Locked quotes are held in-process and lost on restart; callers needing durable quotes should persist them.
/// </summary>
public sealed class CurrencyExchangeService : ICurrencyExchangeService
{
    private readonly ILogger<CurrencyExchangeService> _logger;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, ExchangeRate> _rateCache = new();
    private readonly ConcurrentDictionary<string, ConversionResult> _lockedQuotes = new();
    private readonly TimeSpan _rateCacheDuration = TimeSpan.FromMinutes(15);

    private static readonly IReadOnlyList<CurrencyInfo> CurrencyInfoList = new List<CurrencyInfo>
    {
        new() { Code = BricsCurrency.ZAR, Name = "South African Rand", Symbol = "R",  Country = "South Africa", CountryCode = "ZA", FlagEmoji = "🇿🇦", DecimalPlaces = 2 },
        new() { Code = BricsCurrency.BRL, Name = "Brazilian Real",     Symbol = "R$", Country = "Brazil",       CountryCode = "BR", FlagEmoji = "🇧🇷", DecimalPlaces = 2 },
        new() { Code = BricsCurrency.RUB, Name = "Russian Ruble",      Symbol = "₽",  Country = "Russia",       CountryCode = "RU", FlagEmoji = "🇷🇺", DecimalPlaces = 2 },
        new() { Code = BricsCurrency.INR, Name = "Indian Rupee",       Symbol = "₹",  Country = "India",        CountryCode = "IN", FlagEmoji = "🇮🇳", DecimalPlaces = 2 },
        new() { Code = BricsCurrency.CNY, Name = "Chinese Yuan",       Symbol = "¥",  Country = "China",        CountryCode = "CN", FlagEmoji = "🇨🇳", DecimalPlaces = 2 }
    };

    /// <summary>Static baseline rates with ZAR as the pivot. Used when the live source is unavailable.</summary>
    private static readonly Dictionary<BricsCurrency, decimal> BaselineRatesZar = new()
    {
        { BricsCurrency.ZAR, 1.0m  },
        { BricsCurrency.BRL, 0.27m },
        { BricsCurrency.RUB, 4.85m },
        { BricsCurrency.INR, 4.52m },
        { BricsCurrency.CNY, 0.39m }
    };

    /// <summary>Currencies not supported by frankfurter.app (sanctions, capital controls).</summary>
    private static readonly HashSet<BricsCurrency> UnsupportedByFrankfurter = new() { BricsCurrency.RUB };

    public CurrencyExchangeService(ILogger<CurrencyExchangeService> logger, HttpClient httpClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<ExchangeRate> GetExchangeRateAsync(BricsCurrency from, BricsCurrency to, CancellationToken ct = default)
    {
        var cacheKey = $"{from}_{to}";
        if (_rateCache.TryGetValue(cacheKey, out var cachedRate) &&
            DateTime.UtcNow - cachedRate.RateDate < _rateCacheDuration)
        {
            return cachedRate;
        }

        var fromZarRate = BaselineRatesZar[from];
        var toZarRate = BaselineRatesZar[to];
        var rate = toZarRate / fromZarRate;

        try
        {
            var realTimeRate = await FetchRealTimeRateAsync(from, to, ct).ConfigureAwait(false);
            if (realTimeRate.HasValue) rate = realTimeRate.Value;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch real-time rate for {From}/{To}, using baseline", from, to);
        }

        var exchangeRate = new ExchangeRate
        {
            FromCurrency = from,
            ToCurrency = to,
            Rate = rate,
            RateDate = DateTime.UtcNow,
            Source = "BRICS_EXCHANGE"
        };

        _rateCache[cacheKey] = exchangeRate;
        return exchangeRate;
    }

    public async Task<IReadOnlyList<ExchangeRate>> GetAllRatesAsync(BricsCurrency baseCurrency, CancellationToken ct = default)
    {
        var rates = new List<ExchangeRate>();
        foreach (var currency in Enum.GetValues<BricsCurrency>())
        {
            if (currency != baseCurrency)
                rates.Add(await GetExchangeRateAsync(baseCurrency, currency, ct).ConfigureAwait(false));
        }
        return rates;
    }

    public async Task<ConversionResult> ConvertAsync(decimal amount, BricsCurrency from, BricsCurrency to, CancellationToken ct = default)
    {
        var rate = await GetExchangeRateAsync(from, to, ct).ConfigureAwait(false);
        var converted = amount * rate.Rate;

        var feePercentage = from == to ? 0m : 0.005m;
        var fee = converted * feePercentage;
        var final = converted - fee;

        return new ConversionResult
        {
            OriginalAmount = amount,
            OriginalCurrency = from,
            ConvertedAmount = converted,
            TargetCurrency = to,
            ExchangeRate = rate.Rate,
            Fee = fee,
            FinalAmount = Math.Round(final, 2),
            QuoteExpiry = DateTime.UtcNow.AddMinutes(15),
            QuoteId = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()
        };
    }

    public async Task<ConversionResult> LockRateAsync(decimal amount, BricsCurrency from, BricsCurrency to, int validMinutes = 15, CancellationToken ct = default)
    {
        var result = await ConvertAsync(amount, from, to, ct).ConfigureAwait(false);
        var locked = result with { QuoteExpiry = DateTime.UtcNow.AddMinutes(validMinutes) };
        _lockedQuotes[locked.QuoteId] = locked;
        _logger.LogInformation("Locked exchange quote {QuoteId}: {Amount} {From} -> {FinalAmount} {To} @ {Rate}",
            locked.QuoteId, amount, from, locked.FinalAmount, to, locked.ExchangeRate);
        return locked;
    }

    public Task<ConversionResult> ExecuteLockedConversionAsync(string quoteId, CancellationToken ct = default)
    {
        if (!_lockedQuotes.TryRemove(quoteId, out var quote))
            throw new InvalidOperationException($"Quote {quoteId} not found or already used");

        if (DateTime.UtcNow > quote.QuoteExpiry)
            throw new InvalidOperationException($"Quote {quoteId} has expired");

        _logger.LogInformation("Executed locked conversion {QuoteId}", quoteId);
        return Task.FromResult(quote);
    }

    public CurrencyInfo GetCurrencyInfo(BricsCurrency currency) =>
        CurrencyInfoList.First(c => c.Code == currency);

    public IReadOnlyList<CurrencyInfo> GetAllCurrencies() => CurrencyInfoList;

    private async Task<decimal?> FetchRealTimeRateAsync(BricsCurrency from, BricsCurrency to, CancellationToken ct)
    {
        if (from == to) return 1m;

        if (UnsupportedByFrankfurter.Contains(from) || UnsupportedByFrankfurter.Contains(to))
            return FallbackToBaseline(from, to);

        try
        {
            var url = $"https://api.frankfurter.app/latest?from={from}&to={to}";
            var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Frankfurter API returned {StatusCode} for {From}/{To}, falling back to baseline",
                    (int)response.StatusCode, from, to);
                return FallbackToBaseline(from, to);
            }

            var content = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("rates", out var rates) &&
                rates.TryGetProperty(to.ToString(), out var rateValue))
            {
                return rateValue.GetDecimal();
            }
            return FallbackToBaseline(from, to);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Real-time rate fetch failed for {From}/{To}, falling back to baseline", from, to);
            return FallbackToBaseline(from, to);
        }
    }

    private static decimal? FallbackToBaseline(BricsCurrency from, BricsCurrency to)
    {
        if (BaselineRatesZar.TryGetValue(from, out var fromRate) &&
            BaselineRatesZar.TryGetValue(to, out var toRate) &&
            fromRate != 0)
        {
            return toRate / fromRate;
        }
        return null;
    }
}
