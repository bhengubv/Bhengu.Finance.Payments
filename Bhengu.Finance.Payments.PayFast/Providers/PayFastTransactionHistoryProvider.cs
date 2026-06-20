// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayFast.Providers;

/// <summary>
/// PayFast Transaction History API — pull a merchant's processed transactions over a date range, or for
/// a specific day / week / month, for reconciliation and reporting. PayFast returns these as raw CSV
/// report text, which is returned as-is. Mirrors PayFast's official SDK <c>TransactionHistory</c> service.
/// </summary>
/// <remarks>
/// Signing follows PayFast's REST rule (<see cref="PayFastSignatureHelper.ComputeApiSignature"/>): the
/// query params are part of the signed data; the sandbox <c>testing=true</c> flag is appended to the URL
/// but NOT signed (per PayFast's official Request.php). See PAYFAST_API_REFERENCE.md.
/// </remarks>
public sealed class PayFastTransactionHistoryProvider : BhenguProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly PayFastOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayFast;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PayFastTransactionHistoryProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        ILogger<PayFastTransactionHistoryProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayFastOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri("https://api.payfast.co.za/");
    }

    /// <summary>
    /// Transactions over a date range. <paramref name="from"/> defaults to today; <paramref name="from"/>
    /// and <paramref name="to"/> are swapped automatically if supplied reversed. Returns raw CSV.
    /// </summary>
    public Task<string> GetHistoryAsync(DateOnly? from = null, DateOnly? to = null, int? offset = null, int? limit = null, CancellationToken ct = default)
    {
        var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var toDate = to;
        if (toDate is { } t && t < fromDate)
            (fromDate, toDate) = (t, fromDate);

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["from"] = fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
        if (toDate is { } td)
            query["to"] = td.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        AddPaging(query, offset, limit);

        return RunOperationAsync("transaction_history", () => SendSignedQueryAsync("transactions/history", query, ct), ct);
    }

    /// <summary>Transactions for a single day. Returns raw CSV.</summary>
    public Task<string> GetDailyHistoryAsync(DateOnly date, int? offset = null, int? limit = null, CancellationToken ct = default)
        => GetDatedHistoryAsync("daily", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), offset, limit, ct);

    /// <summary>Transactions for the week containing <paramref name="date"/>. Returns raw CSV.</summary>
    public Task<string> GetWeeklyHistoryAsync(DateOnly date, int? offset = null, int? limit = null, CancellationToken ct = default)
        => GetDatedHistoryAsync("weekly", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), offset, limit, ct);

    /// <summary>Transactions for a calendar month. Returns raw CSV.</summary>
    public Task<string> GetMonthlyHistoryAsync(int year, int month, int? offset = null, int? limit = null, CancellationToken ct = default)
    {
        if (month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), month, "Month must be 1-12.");
        return GetDatedHistoryAsync("monthly", $"{year:D4}-{month:D2}", offset, limit, ct);
    }

    private Task<string> GetDatedHistoryAsync(string segment, string date, int? offset, int? limit, CancellationToken ct)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal) { ["date"] = date };
        AddPaging(query, offset, limit);
        return RunOperationAsync($"transaction_history_{segment}", () => SendSignedQueryAsync($"transactions/history/{segment}", query, ct), ct);
    }

    private static void AddPaging(IDictionary<string, string> query, int? offset, int? limit)
    {
        if (offset is { } o) query["offset"] = o.ToString(CultureInfo.InvariantCulture);
        if (limit is { } l) query["limit"] = l.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string> SendSignedQueryAsync(string path, IDictionary<string, string> query, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var signature = PayFastSignatureHelper.ComputeApiSignature(
            _options.MerchantId, _options.Passphrase ?? string.Empty, timestamp, query);

        var qs = string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var url = qs.Length > 0 ? $"{path}?{qs}" : path;
        if (_options.UseSandbox)
            url += url.Contains('?', StringComparison.Ordinal) ? "&testing=true" : "?testing=true";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("merchant-id", _options.MerchantId);
        req.Headers.Add("version", "v1");
        req.Headers.Add("timestamp", timestamp);
        req.Headers.Add("signature", signature);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayFast transaction history {Path} failed: {Status} {Body}", path, response.StatusCode, body);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }
}
