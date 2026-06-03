// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco implementation of <see cref="ISettlementProvider"/> backed by Yoco's
/// <c>/v1/payouts</c> and <c>/v1/payouts/{id}</c> endpoints. Yoco initiates payouts automatically
/// on a fixed cadence; this provider lets merchants reconcile the resulting batches.
/// </summary>
public sealed class YocoSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;
    private readonly ILogger<YocoSettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Yoco;

    /// <summary>Construct a Yoco settlement provider. Designed to be registered via DI.</summary>
    public YocoSettlementProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoSettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var path = new StringBuilder("payouts?limit=100")
            .Append("&from=").Append(Uri.EscapeDataString(fromUtc.ToString("o", CultureInfo.InvariantCulture)))
            .Append("&to=").Append(Uri.EscapeDataString(toUtc.ToString("o", CultureInfo.InvariantCulture)))
            .ToString();

        var body = await SendAsync(HttpMethod.Get, path, ct, "ListSettlements").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<YocoPayoutListResponse>(body);
        if (response?.Data is null)
            return Array.Empty<Settlement>();

        var result = new List<Settlement>(response.Data.Count);
        foreach (var p in response.Data)
            result.Add(MapSettlement(p));
        return result;
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        try
        {
            var body = await SendAsync(HttpMethod.Get, $"payouts/{Uri.EscapeDataString(settlementReference)}", ct, "GetSettlement").ConfigureAwait(false);
            var payout = JsonSerializer.Deserialize<YocoPayoutData>(body);
            return payout is not null ? MapSettlement(payout) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        var body = await SendAsync(
            HttpMethod.Get,
            $"payouts/{Uri.EscapeDataString(settlementReference)}/transactions?limit=100",
            ct,
            "ListSettlementTransactions").ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<YocoPayoutTransactionListResponse>(body);
        if (response?.Data is null)
            return Array.Empty<SettlementTransaction>();

        var result = new List<SettlementTransaction>(response.Data.Count);
        foreach (var tx in response.Data)
            result.Add(MapTransaction(tx));
        return result;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Yoco failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
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
        [JsonPropertyName("data")] public List<YocoPayoutData>? Data { get; set; }
    }

    private sealed class YocoPayoutData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("amountInCents")] public int AmountInCents { get; set; }
        [JsonPropertyName("netAmountInCents")] public int? NetAmountInCents { get; set; }
        [JsonPropertyName("feeInCents")] public int? FeeInCents { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("bankAccountId")] public string? BankAccountId { get; set; }
        [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("paidAt")] public DateTime? PaidAt { get; set; }
    }

    private sealed class YocoPayoutTransactionListResponse
    {
        [JsonPropertyName("data")] public List<YocoPayoutTransactionData>? Data { get; set; }
    }

    private sealed class YocoPayoutTransactionData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("sourceId")] public string? SourceId { get; set; }
        [JsonPropertyName("amountInCents")] public int AmountInCents { get; set; }
        [JsonPropertyName("netAmountInCents")] public int? NetAmountInCents { get; set; }
        [JsonPropertyName("feeInCents")] public int? FeeInCents { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}
