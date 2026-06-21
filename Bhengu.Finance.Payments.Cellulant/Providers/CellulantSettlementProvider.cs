// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Cellulant.Providers;

/// <summary>
/// Cellulant (Tingg) implementation of <see cref="ISettlementProvider"/>. Surfaces per-service
/// settlement batches and the constituent payment transactions that rolled into each batch.
/// </summary>
/// <remarks>
/// UNVERIFIED: Tingg's public Checkout 3.0 documentation does not describe a programmatic settlement
/// reporting API (settlement reports are delivered via the merchant portal / SFTP per merchant
/// agreement). The <c>settlements/v1</c> paths and response shapes here are retained from prior
/// behaviour and are NOT confirmed against Tingg docs. The host + auth (apiKey + Bearer) ARE verified
/// (https://docs.tingg.africa/reference/authenticate-requests). Do not rely on this in production
/// until verified against your Tingg settlement integration.
/// </remarks>
public sealed class CellulantSettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly CellulantOptions _options;
    private readonly CellulantTokenBroker _tokenBroker;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Cellulant;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public CellulantSettlementProvider(
        HttpClient httpClient,
        IOptions<CellulantOptions> options,
        ILogger<CellulantSettlementProvider> logger,
        CellulantTokenBroker? tokenBroker = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokenBroker = tokenBroker ?? new CellulantTokenBroker(options!, new Microsoft.Extensions.Logging.Abstractions.NullLogger<CellulantTokenBroker>());

        if (string.IsNullOrWhiteSpace(_options.ServiceCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ServiceCode)} is required");

        if (_httpClient.BaseAddress is null)
        {
            // Verified Tingg Checkout 3.0 hosts. Source: https://docs.tingg.africa/reference/authenticate-requests
            var defaultUrl = _options.UseSandbox
                ? "https://api-approval.tingg.africa/"
                : "https://api.tingg.africa/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var path = $"settlements/v1?serviceCode={Uri.EscapeDataString(_options.ServiceCode)}&from={Uri.EscapeDataString(fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}";

        var response = await RunOperationAsync("list_settlements", async () =>
        {
            var body = await SendAuthorisedAsync(HttpMethod.Get, path, null, ct, "ListSettlements").ConfigureAwait(false);
            return JsonSerializer.Deserialize<CellulantSettlementListResponse>(body);
        }, ct).ConfigureAwait(false);

        if (response?.Data is null) yield break;
        foreach (var s in response.Data)
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
                var body = await SendAuthorisedAsync(HttpMethod.Get, $"settlements/v1/{Uri.EscapeDataString(settlementReference)}", null, ct, "GetSettlement").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<CellulantSettlementResponse>(body);
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
            var body = await SendAuthorisedAsync(HttpMethod.Get, $"settlements/v1/{Uri.EscapeDataString(settlementReference)}/transactions", null, ct, "ListSettlementTransactions").ConfigureAwait(false);
            return JsonSerializer.Deserialize<CellulantSettlementTransactionListResponse>(body);
        }, ct).ConfigureAwait(false);

        if (response?.Data is null) yield break;
        foreach (var t in response.Data)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(t);
        }
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
        ct.ThrowIfCancellationRequested();

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Tingg requires the apiKey header on every call (case-insensitive on the wire).
        // Source: https://docs.tingg.africa/reference/authenticate-requests
        if (!string.IsNullOrEmpty(_options.ApiKey))
            req.Headers.TryAddWithoutValidation("apiKey", _options.ApiKey);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Cellulant {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
