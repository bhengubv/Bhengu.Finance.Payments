// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay settlement / reconciliation provider. Wraps the <c>/v1/settlements</c>,
/// <c>/v1/settlements/{id}</c>, and <c>/v1/settlements/recon/combined</c> endpoints.
/// </summary>
/// <remarks>
/// Razorpay settles funds to the merchant's bank account on T+2 (or T+1 with instant settlements).
/// The recon endpoint returns line-items inside a single settlement batch — used for ledger reconciliation.
/// </remarks>
public sealed class RazorpaySettlementProvider : ISettlementProvider
{
    private readonly RazorpayHttpClient _http;
    private readonly ILogger<RazorpaySettlementProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new settlement provider bound to the supplied HTTP client and options.</summary>
    public RazorpaySettlementProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpaySettlementProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new RazorpayHttpClient(httpClient, options.Value, ProviderName, logger);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var fromEpoch = new DateTimeOffset(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var toEpoch = new DateTimeOffset(DateTime.SpecifyKind(toUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

        var path = $"v1/settlements?from={fromEpoch.ToString(CultureInfo.InvariantCulture)}&to={toEpoch.ToString(CultureInfo.InvariantCulture)}&count=100";
        var raw = await _http.GetAsync(path, ct, "ListSettlements").ConfigureAwait(false);
        var collection = RazorpayHttpClient.DeserialiseOrThrow<RazorpaySettlementCollection>(raw, ProviderName, "ListSettlements");

        _logger.LogInformation("Razorpay listed {Count} settlements between {From:o} and {To:o}",
            collection.Items?.Count ?? 0, fromUtc, toUtc);

        if (collection.Items is null) yield break;
        foreach (var s in collection.Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSettlement(s);
        }
    }

    /// <inheritdoc />
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementReference);

        try
        {
            var raw = await _http.GetAsync($"v1/settlements/{Uri.EscapeDataString(settlementReference)}", ct, "GetSettlement").ConfigureAwait(false);
            var s = RazorpayHttpClient.DeserialiseOrThrow<RazorpaySettlement>(raw, ProviderName, "GetSettlement");
            return MapSettlement(s);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementReference);

        // Razorpay returns line items via the combined-recon endpoint filtered by settlement_id.
        var path = $"v1/settlements/recon/combined?settlement_id={Uri.EscapeDataString(settlementReference)}&count=100";
        var raw = await _http.GetAsync(path, ct, "ListSettlementTransactions").ConfigureAwait(false);
        var collection = RazorpayHttpClient.DeserialiseOrThrow<RazorpayReconCollection>(raw, ProviderName, "ListSettlementTransactions");

        if (collection.Items is null) yield break;
        foreach (var t in collection.Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(t);
        }
    }

    private static Settlement MapSettlement(RazorpaySettlement s)
    {
        var gross = (s.Amount + s.Fees + s.Tax) / 100m;
        return new Settlement
        {
            Reference = s.Id ?? string.Empty,
            NetAmount = s.Amount / 100m,
            GrossAmount = gross,
            Fees = (s.Fees + s.Tax) / 100m,
            Currency = "INR",
            SettledAt = s.CreatedAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(s.CreatedAt.Value).UtcDateTime : DateTime.UtcNow,
            BankAccountReference = s.UtrNumber,
            TransactionCount = 0
        };
    }

    private static SettlementTransaction MapTransaction(RazorpayReconItem t)
    {
        var kind = t.Type?.ToLowerInvariant() switch
        {
            "payment" => SettlementTransactionKind.Charge,
            "refund" => SettlementTransactionKind.Refund,
            "dispute" or "chargeback" => SettlementTransactionKind.Chargeback,
            "adjustment" => SettlementTransactionKind.Adjustment,
            "transfer" => SettlementTransactionKind.Other,
            _ => SettlementTransactionKind.Other
        };

        var net = t.Credit - t.Debit;

        return new SettlementTransaction
        {
            GatewayReference = t.EntityId ?? t.SettlementId ?? string.Empty,
            Kind = kind,
            NetAmount = net / 100m,
            GrossAmount = (t.Amount is > 0 ? t.Amount : t.Credit) / 100m,
            Fee = t.Fee / 100m,
            Currency = t.Currency ?? "INR",
            CreatedAt = t.CreatedAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.CreatedAt.Value).UtcDateTime : DateTime.UtcNow
        };
    }

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpaySettlementCollection
    {
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("items")] public List<RazorpaySettlement>? Items { get; set; }
    }

    private sealed class RazorpaySettlement
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("fees")] public long Fees { get; set; }
        [JsonPropertyName("tax")] public long Tax { get; set; }
        [JsonPropertyName("utr")] public string? UtrNumber { get; set; }
        [JsonPropertyName("created_at")] public long? CreatedAt { get; set; }
    }

    private sealed class RazorpayReconCollection
    {
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("items")] public List<RazorpayReconItem>? Items { get; set; }
    }

    private sealed class RazorpayReconItem
    {
        [JsonPropertyName("entity_id")] public string? EntityId { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("debit")] public long Debit { get; set; }
        [JsonPropertyName("credit")] public long Credit { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("fee")] public long Fee { get; set; }
        [JsonPropertyName("tax")] public long Tax { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled_at")] public long? SettledAt { get; set; }
        [JsonPropertyName("settlement_id")] public string? SettlementId { get; set; }
        [JsonPropertyName("created_at")] public long? CreatedAt { get; set; }
    }
}
