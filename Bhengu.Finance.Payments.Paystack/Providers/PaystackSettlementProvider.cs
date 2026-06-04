// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paystack.Providers;

/// <summary>
/// Paystack implementation of <see cref="ISettlementProvider"/> backed by Paystack's
/// <c>/settlement</c> and <c>/settlement/:id/transactions</c> endpoints.
/// </summary>
public sealed class PaystackSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;
    private readonly ILogger<PaystackSettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Paystack;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public PaystackSettlementProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackSettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        PaystackHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var qs = new StringBuilder("settlement?perPage=100")
            .Append("&from=").Append(Uri.EscapeDataString(fromUtc.ToString("o", CultureInfo.InvariantCulture)))
            .Append("&to=").Append(Uri.EscapeDataString(toUtc.ToString("o", CultureInfo.InvariantCulture)));

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Get, qs.ToString(), null, "ListSettlements", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackSettlementListResponse>(responseBody, PaystackHttpClient.Json);
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
        return PaystackObservability.ObserveAsync("get_settlement", () => GetSettlementCoreAsync(settlementReference, ct));
    }

    private async Task<Settlement?> GetSettlementCoreAsync(string settlementReference, CancellationToken ct)
    {
        try
        {
            var responseBody = await PaystackHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, $"settlement/{Uri.EscapeDataString(settlementReference)}", null, "GetSettlement", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaystackSettlementResponse>(responseBody, PaystackHttpClient.Json);
            return response?.Data is { } s ? MapSettlement(s) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Get, $"settlement/{Uri.EscapeDataString(settlementReference)}/transactions?perPage=100", null, "ListSettlementTransactions", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackSettlementTransactionListResponse>(responseBody, PaystackHttpClient.Json);
        if (response?.Data is null) yield break;

        foreach (var tx in response.Data)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(tx);
        }
    }

    private static Settlement MapSettlement(PaystackSettlementData s) => new()
    {
        Reference = s.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        NetAmount = s.TotalAmount / 100m,
        GrossAmount = s.TotalAmount / 100m,
        Fees = (s.TotalAmount - s.NetAmount) / 100m,
        Currency = s.Currency ?? "NGN",
        SettledAt = s.SettledAt ?? s.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = s.SettlementBank,
        TransactionCount = s.TotalTransactions
    };

    private static SettlementTransaction MapTransaction(PaystackSettlementTransactionData tx) => new()
    {
        GatewayReference = tx.Reference ?? string.Empty,
        Kind = MapKind(tx.Channel, tx.Status),
        NetAmount = tx.Amount / 100m,
        GrossAmount = tx.Amount / 100m,
        Fee = tx.Fees / 100m,
        Currency = tx.Currency ?? "NGN",
        CreatedAt = tx.PaidAt ?? tx.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? channel, string? status)
    {
        if (string.Equals(status, "reversed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(status, "refunded", StringComparison.OrdinalIgnoreCase))
            return SettlementTransactionKind.Refund;
        if (string.Equals(channel, "chargeback", StringComparison.OrdinalIgnoreCase))
            return SettlementTransactionKind.Chargeback;
        if (string.Equals(channel, "fee", StringComparison.OrdinalIgnoreCase))
            return SettlementTransactionKind.Fee;
        return SettlementTransactionKind.Charge;
    }

    // === Paystack API shapes (internal) ===

    private sealed class PaystackSettlementResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public PaystackSettlementData? Data { get; set; }
    }

    private sealed class PaystackSettlementListResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public List<PaystackSettlementData>? Data { get; set; }
    }

    private sealed class PaystackSettlementData
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("total_amount")] public long TotalAmount { get; set; }
        [JsonPropertyName("net_amount")] public long NetAmount { get; set; }
        [JsonPropertyName("total_transactions")] public int TotalTransactions { get; set; }
        [JsonPropertyName("settlement_bank")] public string? SettlementBank { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("settled_at")] public DateTime? SettledAt { get; set; }
    }

    private sealed class PaystackSettlementTransactionListResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public List<PaystackSettlementTransactionData>? Data { get; set; }
    }

    private sealed class PaystackSettlementTransactionData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("fees")] public long Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
        [JsonPropertyName("paid_at")] public DateTime? PaidAt { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}
