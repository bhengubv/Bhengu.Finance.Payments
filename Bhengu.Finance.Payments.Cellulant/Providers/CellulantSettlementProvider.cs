// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Cellulant.Providers;

/// <summary>
/// Cellulant (Tingg) implementation of <see cref="ISettlementProvider"/>. Wraps Tingg's
/// <c>/settlements/v1</c> endpoints that surface per-service settlement batches and the
/// constituent payment transactions that rolled into each batch.
/// </summary>
public sealed class CellulantSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly CellulantOptions _options;
    private readonly ILogger<CellulantSettlementProvider> _logger;
    private readonly CellulantTokenBroker _tokenBroker;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Cellulant;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public CellulantSettlementProvider(
        HttpClient httpClient,
        IOptions<CellulantOptions> options,
        ILogger<CellulantSettlementProvider> logger,
        CellulantTokenBroker? tokenBroker = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenBroker = tokenBroker ?? new CellulantTokenBroker(options!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<CellulantTokenBroker>());

        if (string.IsNullOrWhiteSpace(_options.ServiceCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ServiceCode)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://online.uat.tingg.africa/"
                : "https://online.tingg.africa/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "list_settlements");
        var path = $"settlements/v1?serviceCode={Uri.EscapeDataString(_options.ServiceCode)}&from={Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}";

        var body = await SendAuthorisedAsync(HttpMethod.Get, path, null, ct, "ListSettlements").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<CellulantSettlementListResponse>(body);
        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

        if (response?.Data is null) return Array.Empty<Settlement>();
        var result = new List<Settlement>(response.Data.Count);
        foreach (var s in response.Data) result.Add(MapSettlement(s));
        return result;
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "get_settlement");
        try
        {
            var body = await SendAuthorisedAsync(HttpMethod.Get, $"settlements/v1/{Uri.EscapeDataString(settlementReference)}", null, ct, "GetSettlement").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<CellulantSettlementResponse>(body);
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

        var body = await SendAuthorisedAsync(HttpMethod.Get, $"settlements/v1/{Uri.EscapeDataString(settlementReference)}/transactions", null, ct, "ListSettlementTransactions").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<CellulantSettlementTransactionListResponse>(body);
        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

        if (response?.Data is null) return Array.Empty<SettlementTransaction>();
        var result = new List<SettlementTransaction>(response.Data.Count);
        foreach (var t in response.Data) result.Add(MapTransaction(t));
        return result;
    }

    private static Settlement MapSettlement(CellulantSettlementData s) => new()
    {
        Reference = s.SettlementReference ?? string.Empty,
        NetAmount = s.NetAmount ?? s.GrossAmount ?? 0m,
        GrossAmount = s.GrossAmount,
        Fees = s.Fees,
        Currency = s.Currency ?? "KES",
        SettledAt = s.SettledAt ?? s.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccount,
        TransactionCount = s.TransactionCount ?? 0
    };

    private static SettlementTransaction MapTransaction(CellulantSettlementTransactionData t) => new()
    {
        GatewayReference = t.TransactionReference ?? string.Empty,
        Kind = MapKind(t.Kind),
        NetAmount = t.NetAmount ?? t.GrossAmount ?? 0m,
        GrossAmount = t.GrossAmount,
        Fee = t.Fee,
        Currency = t.Currency ?? "KES",
        CreatedAt = t.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "refund" or "reversal" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Charge
    };

    private async Task<string> SendAuthorisedAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        var token = await _tokenBroker.EnsureAccessTokenAsync(_httpClient, ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Cellulant failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Cellulant {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class CellulantSettlementListResponse
    {
        [JsonPropertyName("data")] public List<CellulantSettlementData>? Data { get; set; }
    }

    private sealed class CellulantSettlementResponse
    {
        [JsonPropertyName("settlement")] public CellulantSettlementData? Settlement { get; set; }
    }

    private sealed class CellulantSettlementData
    {
        [JsonPropertyName("settlementReference")] public string? SettlementReference { get; set; }
        [JsonPropertyName("grossAmount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public decimal? NetAmount { get; set; }
        [JsonPropertyName("fees")] public decimal? Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settledAt")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("bankAccount")] public string? BankAccount { get; set; }
        [JsonPropertyName("transactionCount")] public int? TransactionCount { get; set; }
    }

    private sealed class CellulantSettlementTransactionListResponse
    {
        [JsonPropertyName("data")] public List<CellulantSettlementTransactionData>? Data { get; set; }
    }

    private sealed class CellulantSettlementTransactionData
    {
        [JsonPropertyName("transactionReference")] public string? TransactionReference { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("grossAmount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public decimal? NetAmount { get; set; }
        [JsonPropertyName("fee")] public decimal? Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}
