// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.BricsPay.Providers;

/// <summary>
/// BRICS Pay implementation of <see cref="ISettlementProvider"/>. Wraps the BRICS Pay
/// <c>/settlements</c> feed which surfaces the per-day cross-border settlement batches credited
/// to the merchant's home-currency account.
/// </summary>
public sealed class BricsPaySettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly BricsPayOptions _options;
    private readonly ILogger<BricsPaySettlementProvider> _logger;
    private readonly string _baseUrl;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.BricsPay;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public BricsPaySettlementProvider(
        HttpClient httpClient,
        IOptions<BricsPayOptions> options,
        ILogger<BricsPaySettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.SecretKey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://sandbox.bricspay.org/api/v1")
            : (_options.BaseUrl ?? "https://api.bricspay.org/api/v1");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlements");
        var qs = $"?merchant_id={Uri.EscapeDataString(_options.MerchantId)}&from={Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}";

        var body = await SendAsync(HttpMethod.Get, $"{_baseUrl}/settlements{qs}", null, ct, "ListSettlements").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<BricsPaySettlementListResponse>(body);
        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
        if (response?.Settlements is null) return Array.Empty<Settlement>();

        var result = new List<Settlement>(response.Settlements.Count);
        foreach (var s in response.Settlements) result.Add(MapSettlement(s));
        return result;
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "get_settlement");
        try
        {
            var body = await SendAsync(HttpMethod.Get, $"{_baseUrl}/settlements/{Uri.EscapeDataString(settlementReference)}", null, ct, "GetSettlement").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<BricsPaySettlementResponse>(body);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return response?.Settlement is { } s ? MapSettlement(s) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlement_transactions");

        var body = await SendAsync(HttpMethod.Get, $"{_baseUrl}/settlements/{Uri.EscapeDataString(settlementReference)}/transactions", null, ct, "ListSettlementTransactions").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<BricsPaySettlementTransactionListResponse>(body);
        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
        if (response?.Transactions is null) return Array.Empty<SettlementTransaction>();

        var result = new List<SettlementTransaction>(response.Transactions.Count);
        foreach (var t in response.Transactions) result.Add(MapTransaction(t));
        return result;
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

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to BRICS Pay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("BRICS Pay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
