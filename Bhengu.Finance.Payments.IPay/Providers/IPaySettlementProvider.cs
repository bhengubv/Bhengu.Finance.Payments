// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.IPay.Configuration;
using Bhengu.Finance.Payments.IPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.IPay.Providers;

/// <summary>
/// iPay (Kenya / Africa) settlement provider — wraps the <c>/api/v3/settlements</c> feed.
/// <para>
/// iPay surfaces settlements via the <c>settlements</c> endpoint with date filters. Constituent
/// transactions are reachable via <c>settlements/{id}/transactions</c>.
/// </para>
/// </summary>
public sealed class IPaySettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly IPayOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.IPay;

    /// <summary>Construct the iPay settlement provider. Designed to be registered via DI.</summary>
    public IPaySettlementProvider(
        HttpClient httpClient,
        IOptions<IPayOptions> options,
        ILogger<IPaySettlementProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.VendorId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(IPayOptions.VendorId)} is required");
        if (string.IsNullOrWhiteSpace(_options.HashKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(IPayOptions.HashKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://payments.ipayafrica.com/v3/ke"
                : _options.BaseUrl ?? "https://payments.ipayafrica.com/v3/ke";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<IPaySettlementData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list"))
        {
            try
            {
                var hash = IPayCrypto.ComputeHmacHex($"{_options.VendorId}{fromUtc:yyyy-MM-dd}{toUtc:yyyy-MM-dd}", _options.HashKey);
                var path = $"api/v3/settlements?vid={Uri.EscapeDataString(_options.VendorId)}&from={fromUtc:yyyy-MM-dd}&to={toUtc:yyyy-MM-dd}&hash={hash}";
                var responseBody = await IPayHttpClient.SendGetAsync(_httpClient, Logger, path, ct, "ListSettlements").ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<IPaySettlementListResponse>(responseBody);
                items = envelope?.Data;

                Logger.LogInformation("iPay settlements listed: {Count} between {From:O} and {To:O}", items?.Count ?? 0, fromUtc, toUtc);
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            }
            catch (Exception ex)
            {
                activity.SetOutcome(ClassifyOutcome(ex));
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var d in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToSettlement(d);
        }
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.get");
        try
        {
            var hash = IPayCrypto.ComputeHmacHex($"{_options.VendorId}{settlementReference}", _options.HashKey);
            var path = $"api/v3/settlements/{Uri.EscapeDataString(settlementReference)}?vid={Uri.EscapeDataString(_options.VendorId)}&hash={hash}";
            var responseBody = await IPayHttpClient.SendGetAsync(_httpClient, Logger, path, ct, "GetSettlement").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<IPaySettlementSingleResponse>(responseBody);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return envelope?.Data is null ? null : ToSettlement(envelope.Data);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return null;
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        List<IPayTransactionData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list_transactions"))
        {
            try
            {
                var hash = IPayCrypto.ComputeHmacHex($"{_options.VendorId}{settlementReference}", _options.HashKey);
                var path = $"api/v3/settlements/{Uri.EscapeDataString(settlementReference)}/transactions?vid={Uri.EscapeDataString(_options.VendorId)}&hash={hash}";
                var responseBody = await IPayHttpClient.SendGetAsync(_httpClient, Logger, path, ct, "ListSettlementTransactions").ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<IPayTransactionListResponse>(responseBody);
                items = envelope?.Data;
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            }
            catch (Exception ex)
            {
                activity.SetOutcome(ClassifyOutcome(ex));
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var d in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return ToTransaction(d);
        }
    }

    private Settlement ToSettlement(IPaySettlementData d) => new()
    {
        Reference = d.SettlementId ?? string.Empty,
        NetAmount = d.NetAmount,
        GrossAmount = d.GrossAmount == 0 ? null : d.GrossAmount,
        Fees = d.Charges == 0 ? null : d.Charges,
        Currency = d.Currency ?? _options.Currency,
        SettledAt = d.SettledAt ?? DateTime.UtcNow,
        BankAccountReference = d.BankAccount,
        TransactionCount = d.TransactionCount
    };

    private SettlementTransaction ToTransaction(IPayTransactionData d) => new()
    {
        GatewayReference = d.Txncd ?? d.Oid ?? string.Empty,
        Kind = MapKind(d.TransactionType, d.Amount),
        NetAmount = d.NetAmount == 0 ? d.Amount : d.NetAmount,
        GrossAmount = d.Amount == 0 ? null : d.Amount,
        Fee = d.Charges == 0 ? null : d.Charges,
        Currency = d.Currency ?? _options.Currency,
        CreatedAt = d.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? type, decimal amount) =>
        type?.ToLowerInvariant() switch
        {
            "refund" or "refunded" => SettlementTransactionKind.Refund,
            "chargeback" => SettlementTransactionKind.Chargeback,
            "fee" or "charge" => SettlementTransactionKind.Fee,
            "adjustment" => SettlementTransactionKind.Adjustment,
            _ when amount < 0 => SettlementTransactionKind.Refund,
            _ => SettlementTransactionKind.Charge
        };

    private static string ClassifyOutcome(Exception ex) => ex switch
    {
        PaymentDeclinedException => BhenguPaymentDiagnostics.Outcomes.Declined,
        ProviderRateLimitException => BhenguPaymentDiagnostics.Outcomes.RateLimited,
        ProviderUnavailableException => BhenguPaymentDiagnostics.Outcomes.Unavailable,
        _ => BhenguPaymentDiagnostics.Outcomes.Error
    };

    // === iPay API shapes (internal) ===

    private sealed class IPaySettlementListResponse
    {
        [JsonPropertyName("data")] public List<IPaySettlementData>? Data { get; set; }
    }

    private sealed class IPaySettlementSingleResponse
    {
        [JsonPropertyName("data")] public IPaySettlementData? Data { get; set; }
    }

    private sealed class IPaySettlementData
    {
        [JsonPropertyName("settlement_id")] public string? SettlementId { get; set; }
        [JsonPropertyName("net_amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("gross_amount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("charges")] public decimal Charges { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled_at")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("bank_account")] public string? BankAccount { get; set; }
        [JsonPropertyName("transaction_count")] public int TransactionCount { get; set; }
    }

    private sealed class IPayTransactionListResponse
    {
        [JsonPropertyName("data")] public List<IPayTransactionData>? Data { get; set; }
    }

    private sealed class IPayTransactionData
    {
        [JsonPropertyName("txncd")] public string? Txncd { get; set; }
        [JsonPropertyName("oid")] public string? Oid { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("net_amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("charges")] public decimal Charges { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("transaction_type")] public string? TransactionType { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}
