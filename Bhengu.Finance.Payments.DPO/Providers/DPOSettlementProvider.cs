// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.DPO.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.DPO.Providers;

/// <summary>
/// DPO Group implementation of <see cref="ISettlementProvider"/>. Wraps DPO's settlement-feed
/// endpoint <c>api/v6/settlements</c> which returns merchant-account settlements grouped per
/// payout-currency.
/// </summary>
/// <remarks>
/// DPO uses the same JSON-over-HTTP envelope as the rest of the v6 API: every request includes
/// <c>CompanyToken</c> and a <c>Request</c> discriminator. Result code "000" means success;
/// non-"000" responses surface as <see cref="PaymentDeclinedException"/> from the shared sender.
/// </remarks>
public sealed class DPOSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly DPOOptions _options;
    private readonly ILogger<DPOSettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.DPO;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public DPOSettlementProvider(
        HttpClient httpClient,
        IOptions<DPOOptions> options,
        ILogger<DPOSettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.CompanyToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(DPOOptions.CompanyToken)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://secure1.sandbox.directpay.online/"
                : "https://secure.3gdirectpay.com/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlements");

        var body = await SendAsync(new
        {
            CompanyToken = _options.CompanyToken,
            Request = "getSettlementsList",
            DateFrom = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTo = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }, ct, "ListSettlements").ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<DPOSettlementListResponse>(body);
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
            var body = await SendAsync(new
            {
                CompanyToken = _options.CompanyToken,
                Request = "getSettlement",
                SettlementReference = settlementReference
            }, ct, "GetSettlement").ConfigureAwait(false);

            var response = JsonSerializer.Deserialize<DPOSettlementResponse>(body);
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

        var body = await SendAsync(new
        {
            CompanyToken = _options.CompanyToken,
            Request = "getSettlementTransactions",
            SettlementReference = settlementReference
        }, ct, "ListSettlementTransactions").ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<DPOSettlementTransactionListResponse>(body);
        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
        if (response?.Transactions is null) return Array.Empty<SettlementTransaction>();

        var result = new List<SettlementTransaction>(response.Transactions.Count);
        foreach (var t in response.Transactions) result.Add(MapTransaction(t));
        return result;
    }

    private static Settlement MapSettlement(DPOSettlementData s) => new()
    {
        Reference = s.SettlementReference ?? string.Empty,
        NetAmount = ParseAmount(s.NetAmount) ?? ParseAmount(s.GrossAmount) ?? 0m,
        GrossAmount = ParseAmount(s.GrossAmount),
        Fees = ParseAmount(s.Fees),
        Currency = s.Currency ?? "USD",
        SettledAt = ParseDate(s.SettlementDate) ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccount,
        TransactionCount = s.TransactionCount ?? 0
    };

    private static SettlementTransaction MapTransaction(DPOSettlementTransactionData t) => new()
    {
        GatewayReference = t.TransactionToken ?? t.TransID ?? string.Empty,
        Kind = MapKind(t.Kind),
        NetAmount = ParseAmount(t.NetAmount) ?? ParseAmount(t.GrossAmount) ?? 0m,
        GrossAmount = ParseAmount(t.GrossAmount),
        Fee = ParseAmount(t.Fee),
        Currency = t.Currency ?? "USD",
        CreatedAt = ParseDate(t.TransactionDate) ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "refund" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Charge
    };

    private static decimal? ParseAmount(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTime? ParseDate(string? raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v) ? v : null;

    private async Task<string> SendAsync(object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v6/")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to DPO failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("DPO {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class DPOSettlementListResponse
    {
        [JsonPropertyName("Result")] public string? Result { get; set; }
        [JsonPropertyName("Settlements")] public List<DPOSettlementData>? Settlements { get; set; }
    }

    private sealed class DPOSettlementResponse
    {
        [JsonPropertyName("Result")] public string? Result { get; set; }
        [JsonPropertyName("Settlement")] public DPOSettlementData? Settlement { get; set; }
    }

    private sealed class DPOSettlementData
    {
        [JsonPropertyName("SettlementReference")] public string? SettlementReference { get; set; }
        [JsonPropertyName("GrossAmount")] public string? GrossAmount { get; set; }
        [JsonPropertyName("NetAmount")] public string? NetAmount { get; set; }
        [JsonPropertyName("Fees")] public string? Fees { get; set; }
        [JsonPropertyName("Currency")] public string? Currency { get; set; }
        [JsonPropertyName("SettlementDate")] public string? SettlementDate { get; set; }
        [JsonPropertyName("BankAccount")] public string? BankAccount { get; set; }
        [JsonPropertyName("TransactionCount")] public int? TransactionCount { get; set; }
    }

    private sealed class DPOSettlementTransactionListResponse
    {
        [JsonPropertyName("Result")] public string? Result { get; set; }
        [JsonPropertyName("Transactions")] public List<DPOSettlementTransactionData>? Transactions { get; set; }
    }

    private sealed class DPOSettlementTransactionData
    {
        [JsonPropertyName("TransactionToken")] public string? TransactionToken { get; set; }
        [JsonPropertyName("TransID")] public string? TransID { get; set; }
        [JsonPropertyName("Kind")] public string? Kind { get; set; }
        [JsonPropertyName("GrossAmount")] public string? GrossAmount { get; set; }
        [JsonPropertyName("NetAmount")] public string? NetAmount { get; set; }
        [JsonPropertyName("Fee")] public string? Fee { get; set; }
        [JsonPropertyName("Currency")] public string? Currency { get; set; }
        [JsonPropertyName("TransactionDate")] public string? TransactionDate { get; set; }
    }
}
