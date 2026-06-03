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
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Flutterwave.Providers;

/// <summary>
/// Flutterwave settlement provider — wraps <c>/v3/settlements</c> for reconciliation feeds.
/// <para>
/// Flutterwave's settlement endpoint returns the merchant-account settlement batches Flutterwave
/// has paid out to the merchant's bank account. The constituent transactions inside a batch are
/// reachable via the <c>?settlement_id=</c> filter on <c>/v3/transactions</c>.
/// </para>
/// </summary>
public sealed class FlutterwaveSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;
    private readonly ILogger<FlutterwaveSettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Flutterwave;

    /// <summary>Construct the provider; configures Bearer auth on the injected <paramref name="httpClient"/>.</summary>
    public FlutterwaveSettlementProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwaveSettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FlutterwaveOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.flutterwave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var query = $"from={fromUtc:yyyy-MM-dd}&to={toUtc:yyyy-MM-dd}";
        var responseBody = await SendAsync(HttpMethod.Get, $"v3/settlements?{query}", body: null, ct, "ListSettlements").ConfigureAwait(false);
        var fw = JsonSerializer.Deserialize<FlutterwaveSettlementListResponse>(responseBody);
        if (fw?.Data is null) return Array.Empty<Settlement>();

        var settlements = new List<Settlement>(fw.Data.Count);
        foreach (var d in fw.Data)
            settlements.Add(ToSettlement(d));

        _logger.LogInformation("Flutterwave settlements listed: {Count} between {From:O} and {To:O}", settlements.Count, fromUtc, toUtc);
        return settlements;
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        try
        {
            var responseBody = await SendAsync(HttpMethod.Get, $"v3/settlements/{Uri.EscapeDataString(settlementReference)}", body: null, ct, "GetSettlement").ConfigureAwait(false);
            var fw = JsonSerializer.Deserialize<FlutterwaveSettlementResponse>(responseBody);
            return fw?.Data is null ? null : ToSettlement(fw.Data);
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
        var responseBody = await SendAsync(HttpMethod.Get, $"v3/transactions?settlement_id={Uri.EscapeDataString(settlementReference)}", body: null, ct, "ListSettlementTransactions").ConfigureAwait(false);
        var fw = JsonSerializer.Deserialize<FlutterwaveTransactionListResponse>(responseBody);
        if (fw?.Data is null) return Array.Empty<SettlementTransaction>();

        var txns = new List<SettlementTransaction>(fw.Data.Count);
        foreach (var d in fw.Data)
            txns.Add(ToTransaction(d));
        return txns;
    }

    private static Settlement ToSettlement(FlutterwaveSettlementData d) => new()
    {
        Reference = d.Id.ToString(CultureInfo.InvariantCulture),
        NetAmount = d.AmountSettled,
        GrossAmount = d.GrossAmount == 0 ? null : d.GrossAmount,
        Fees = d.Fee == 0 ? null : d.Fee,
        Currency = d.Currency ?? string.Empty,
        SettledAt = d.SettledAt ?? d.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = d.AccountNumber,
        TransactionCount = d.TransactionCount
    };

    private static SettlementTransaction ToTransaction(FlutterwaveTransactionData d) => new()
    {
        GatewayReference = d.Reference ?? d.TxRef ?? d.Id.ToString(CultureInfo.InvariantCulture),
        Kind = MapTransactionKind(d.Type, d.Amount),
        NetAmount = d.AmountSettled == 0 ? d.Amount : d.AmountSettled,
        GrossAmount = d.Amount == 0 ? null : d.Amount,
        Fee = d.AppFee == 0 ? null : d.AppFee,
        Currency = d.Currency ?? string.Empty,
        CreatedAt = d.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapTransactionKind(string? type, decimal amount) =>
        (type?.ToLowerInvariant(), amount) switch
        {
            ("refund", _)       => SettlementTransactionKind.Refund,
            ("chargeback", _)   => SettlementTransactionKind.Chargeback,
            ("fee", _)          => SettlementTransactionKind.Fee,
            ("adjustment", _)   => SettlementTransactionKind.Adjustment,
            ("debit", _) when amount < 0 => SettlementTransactionKind.Refund,
            _                   => SettlementTransactionKind.Charge
        };

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Flutterwave failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Flutterwave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    // === Flutterwave response shapes (internal) ===

    private sealed class FlutterwaveSettlementListResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public List<FlutterwaveSettlementData>? Data { get; set; }
    }

    private sealed class FlutterwaveSettlementResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public FlutterwaveSettlementData? Data { get; set; }
    }

    private sealed class FlutterwaveSettlementData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("amount_settled")] public decimal AmountSettled { get; set; }
        [JsonPropertyName("gross_amount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("fee")] public decimal Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled_at")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("account_number")] public string? AccountNumber { get; set; }
        [JsonPropertyName("transaction_count")] public int TransactionCount { get; set; }
        [JsonPropertyName("status")] public string? StatusName { get; set; }
    }

    private sealed class FlutterwaveTransactionListResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("data")] public List<FlutterwaveTransactionData>? Data { get; set; }
    }

    private sealed class FlutterwaveTransactionData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tx_ref")] public string? TxRef { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("amount_settled")] public decimal AmountSettled { get; set; }
        [JsonPropertyName("app_fee")] public decimal AppFee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}
