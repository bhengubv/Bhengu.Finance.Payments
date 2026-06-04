// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.BricsPay.Providers;

/// <summary>
/// BRICS Pay implementation of <see cref="ISettlementProvider"/>. Wraps the BRICS Pay
/// <c>/settlements</c> feed which surfaces the per-day cross-border settlement batches credited
/// to the merchant's home-currency account.
/// </summary>
public sealed class BricsPaySettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly BricsPayOptions _options;
    private readonly string _baseUrl;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.BricsPay;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public BricsPaySettlementProvider(
        HttpClient httpClient,
        IOptions<BricsPayOptions> options,
        ILogger<BricsPaySettlementProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.SecretKey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://sandbox.bricspay.org/api/v1")
            : (_options.BaseUrl ?? "https://api.bricspay.org/api/v1");
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var qs = $"?merchant_id={Uri.EscapeDataString(_options.MerchantId)}&from={Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}";

        var response = await RunOperationAsync("list_settlements", async () =>
        {
            var body = await SendAsync(HttpMethod.Get, $"{_baseUrl}/settlements{qs}", null, ct, "ListSettlements").ConfigureAwait(false);
            return JsonSerializer.Deserialize<BricsPaySettlementListResponse>(body);
        }, ct).ConfigureAwait(false);

        if (response?.Settlements is null) yield break;
        foreach (var s in response.Settlements)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSettlement(s);
        }
    }

    /// <inheritdoc/>
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return RunOperationAsync("get_settlement", async () =>
        {
            try
            {
                var body = await SendAsync(HttpMethod.Get, $"{_baseUrl}/settlements/{Uri.EscapeDataString(settlementReference)}", null, ct, "GetSettlement").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<BricsPaySettlementResponse>(body);
                return response?.Settlement is { } s ? MapSettlement(s) : null;
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        var response = await RunOperationAsync("list_settlement_transactions", async () =>
        {
            var body = await SendAsync(HttpMethod.Get, $"{_baseUrl}/settlements/{Uri.EscapeDataString(settlementReference)}/transactions", null, ct, "ListSettlementTransactions").ConfigureAwait(false);
            return JsonSerializer.Deserialize<BricsPaySettlementTransactionListResponse>(body);
        }, ct).ConfigureAwait(false);

        if (response?.Transactions is null) yield break;
        foreach (var t in response.Transactions)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(t);
        }
    }

    private static Settlement MapSettlement(BricsPaySettlementData s) => new()
    {
        Reference = s.SettlementReference ?? string.Empty,
        NetAmount = s.NetAmount,
        GrossAmount = s.GrossAmount,
        Fees = s.Fees,
        Currency = s.Currency ?? "ZAR",
        SettledAt = s.SettledAt ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccount,
        TransactionCount = s.TransactionCount ?? 0
    };

    private static SettlementTransaction MapTransaction(BricsPaySettlementTransactionData t) => new()
    {
        GatewayReference = t.TransactionReference ?? string.Empty,
        Kind = MapKind(t.Kind),
        NetAmount = t.NetAmount,
        GrossAmount = t.GrossAmount,
        Fee = t.Fee,
        Currency = t.Currency ?? "ZAR",
        CreatedAt = t.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "refund" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Charge
    };

    private async Task<string> SendAsync(HttpMethod method, string url, object? body, CancellationToken ct, string operation)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        var signature = GenerateSignature(json, timestamp);

        using var req = new HttpRequestMessage(method, url);
        if (body is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        req.Headers.Add("X-Merchant-Id", _options.MerchantId);
        req.Headers.Add("X-Signature", signature);
        req.Headers.Add("X-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("BRICS Pay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private string GenerateSignature(string serializedBody, long timestamp)
    {
        var payload = serializedBody + timestamp + _options.SecretKey;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private sealed class BricsPaySettlementListResponse
    {
        [JsonPropertyName("settlements")] public List<BricsPaySettlementData>? Settlements { get; set; }
    }

    private sealed class BricsPaySettlementResponse
    {
        [JsonPropertyName("settlement")] public BricsPaySettlementData? Settlement { get; set; }
    }

    private sealed class BricsPaySettlementData
    {
        [JsonPropertyName("settlement_reference")] public string? SettlementReference { get; set; }
        [JsonPropertyName("gross_amount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("net_amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fees")] public decimal? Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled_at")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("bank_account")] public string? BankAccount { get; set; }
        [JsonPropertyName("transaction_count")] public int? TransactionCount { get; set; }
    }

    private sealed class BricsPaySettlementTransactionListResponse
    {
        [JsonPropertyName("transactions")] public List<BricsPaySettlementTransactionData>? Transactions { get; set; }
    }

    private sealed class BricsPaySettlementTransactionData
    {
        [JsonPropertyName("transaction_reference")] public string? TransactionReference { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("gross_amount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("net_amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fee")] public decimal? Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}
