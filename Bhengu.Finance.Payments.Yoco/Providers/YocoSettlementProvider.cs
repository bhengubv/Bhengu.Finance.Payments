// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco implementation of <see cref="ISettlementProvider"/> backed by Yoco's
/// <c>/v1/payouts</c> and <c>/v1/payouts/{id}</c> endpoints. Yoco initiates payouts automatically
/// on a fixed cadence; this provider lets merchants reconcile the resulting batches.
/// </summary>
public sealed class YocoSettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Yoco;

    /// <summary>Construct a Yoco settlement provider. Designed to be registered via DI.</summary>
    public YocoSettlementProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoSettlementProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        // Yoco's /v1/payouts is offset-paginated via a "next" URL on the envelope; we honour it
        // and yield while iterating so the consumer can stop mid-stream without buffering everything.
        var path = new StringBuilder("payouts?limit=100")
            .Append("&from=").Append(Uri.EscapeDataString(fromUtc.ToString("o", CultureInfo.InvariantCulture)))
            .Append("&to=").Append(Uri.EscapeDataString(toUtc.ToString("o", CultureInfo.InvariantCulture)))
            .ToString();

        while (!string.IsNullOrEmpty(path))
        {
            var body = await SendAsync(HttpMethod.Get, path, ct, "ListSettlements").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<YocoPayoutListResponse>(body, s_jsonOptions);
            if (response?.Data is null) yield break;

            foreach (var p in response.Data)
                yield return MapSettlement(p);

            // Yoco returns a `next` cursor as a fully-qualified URL when more pages exist.
            path = response.Next is { Length: > 0 } next ? StripBase(next) : null;
        }
    }

    /// <inheritdoc/>
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return RunOperationAsync("get_settlement", () => GetSettlementCoreAsync(settlementReference, ct), ct);
    }

    private async Task<Settlement?> GetSettlementCoreAsync(string settlementReference, CancellationToken ct)
    {
        try
        {
            var body = await SendAsync(HttpMethod.Get, $"payouts/{Uri.EscapeDataString(settlementReference)}", ct, "GetSettlement").ConfigureAwait(false);
            var payout = JsonSerializer.Deserialize<YocoPayoutData>(body, s_jsonOptions);
            return payout is not null ? MapSettlement(payout) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(
        string settlementReference,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        ct.ThrowIfCancellationRequested();

        var path = $"payouts/{Uri.EscapeDataString(settlementReference)}/transactions?limit=100";

        while (!string.IsNullOrEmpty(path))
        {
            var body = await SendAsync(HttpMethod.Get, path, ct, "ListSettlementTransactions").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<YocoPayoutTransactionListResponse>(body, s_jsonOptions);
            if (response?.Data is null) yield break;

            foreach (var tx in response.Data)
                yield return MapTransaction(tx);

            path = response.Next is { Length: > 0 } next ? StripBase(next) : null;
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>Strip the absolute base address from a "next" URL so HttpClient can resolve it as a relative path.</summary>
    private string? StripBase(string nextUrl)
    {
        if (string.IsNullOrEmpty(nextUrl)) return null;
        if (_httpClient.BaseAddress is null) return nextUrl;
        var baseUrl = _httpClient.BaseAddress.ToString();
        return nextUrl.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase)
            ? nextUrl[baseUrl.Length..]
            : nextUrl;
    }

    private static Settlement MapSettlement(YocoPayoutData p) => new()
    {
        Reference = p.Id ?? string.Empty,
        NetAmount = (p.NetAmountInCents ?? p.AmountInCents) / 100m,
        GrossAmount = p.AmountInCents / 100m,
        Fees = p.FeeInCents.HasValue ? p.FeeInCents.Value / 100m : null,
        Currency = (p.Currency ?? "ZAR").ToUpperInvariant(),
        SettledAt = p.PaidAt ?? p.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = p.BankAccountId,
        TransactionCount = p.TransactionCount
    };

    private static SettlementTransaction MapTransaction(YocoPayoutTransactionData tx) => new()
    {
        GatewayReference = tx.SourceId ?? tx.Id ?? string.Empty,
        Kind = MapKind(tx.Type),
        NetAmount = (tx.NetAmountInCents ?? tx.AmountInCents) / 100m,
        GrossAmount = tx.AmountInCents / 100m,
        Fee = tx.FeeInCents.HasValue ? tx.FeeInCents.Value / 100m : null,
        Currency = (tx.Currency ?? "ZAR").ToUpperInvariant(),
        CreatedAt = tx.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? type) => type?.ToLowerInvariant() switch
    {
        "charge" or "payment" or "sale" => SettlementTransactionKind.Charge,
        "refund" => SettlementTransactionKind.Refund,
        "chargeback" or "dispute" => SettlementTransactionKind.Chargeback,
        "fee" or "adjustment_fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Other
    };

    // === Yoco API shapes (internal) ===

    private sealed class YocoPayoutListResponse
    {
        public List<YocoPayoutData>? Data { get; set; }
        public string? Next { get; set; }
    }

    private sealed class YocoPayoutData
    {
        public string? Id { get; set; }
        public int AmountInCents { get; set; }
        public int? NetAmountInCents { get; set; }
        public int? FeeInCents { get; set; }
        public string? Currency { get; set; }
        public string? Status { get; set; }
        public string? BankAccountId { get; set; }
        public int TransactionCount { get; set; }
        public DateTime? CreatedAt { get; set; }
        public DateTime? PaidAt { get; set; }
    }

    private sealed class YocoPayoutTransactionListResponse
    {
        public List<YocoPayoutTransactionData>? Data { get; set; }
        public string? Next { get; set; }
    }

    private sealed class YocoPayoutTransactionData
    {
        public string? Id { get; set; }
        public string? SourceId { get; set; }
        public int AmountInCents { get; set; }
        public int? NetAmountInCents { get; set; }
        public int? FeeInCents { get; set; }
        public string? Currency { get; set; }
        public string? Type { get; set; }
        public DateTime? CreatedAt { get; set; }
    }
}
