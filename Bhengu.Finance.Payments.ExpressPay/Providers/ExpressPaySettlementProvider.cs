// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ExpressPay.Providers;

/// <summary>
/// ExpressPay settlement provider — wraps the <c>settlements.php</c> reconciliation feed.
/// <para>
/// ExpressPay exposes settlements via a date-range form post and constituent transactions via the
/// settlement-id form post. Both endpoints accept the standard merchant-id / api-key form fields.
/// </para>
/// </summary>
public sealed class ExpressPaySettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly ExpressPayOptions _options;
    private readonly ILogger<ExpressPaySettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.ExpressPay;

    /// <summary>Construct the ExpressPay settlement provider. Designed to be registered via DI.</summary>
    public ExpressPaySettlementProvider(
        HttpClient httpClient,
        IOptions<ExpressPayOptions> options,
        ILogger<ExpressPaySettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://sandbox.expresspaygh.com/api/"
                : _options.BaseUrl ?? "https://expresspay.com.gh/api/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list");
        try
        {
            var form = new Dictionary<string, string>
            {
                ["merchant-id"] = _options.MerchantId,
                ["api-key"] = _options.ApiKey,
                ["from"] = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["to"] = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };

            var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, _logger, HttpMethod.Post, "settlements.php", form, ct, "ListSettlements").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<ExpressPaySettlementListResponse>(responseBody);
            if (envelope?.Settlements is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
                return Array.Empty<Settlement>();
            }

            var settlements = new List<Settlement>(envelope.Settlements.Count);
            foreach (var d in envelope.Settlements)
                settlements.Add(ToSettlement(d));

            _logger.LogInformation("ExpressPay settlements listed: {Count} between {From:O} and {To:O}", settlements.Count, fromUtc, toUtc);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return settlements;
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.get");
        try
        {
            var form = new Dictionary<string, string>
            {
                ["merchant-id"] = _options.MerchantId,
                ["api-key"] = _options.ApiKey,
                ["settlement-id"] = settlementReference
            };

            var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, _logger, HttpMethod.Post, "settlement.php", form, ct, "GetSettlement").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<ExpressPaySettlementSingleResponse>(responseBody);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return envelope?.Settlement is null ? null : ToSettlement(envelope.Settlement);
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
    public async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list_transactions");
        try
        {
            var form = new Dictionary<string, string>
            {
                ["merchant-id"] = _options.MerchantId,
                ["api-key"] = _options.ApiKey,
                ["settlement-id"] = settlementReference
            };

            var responseBody = await ExpressPayHttpClient.SendFormAsync(_httpClient, _logger, HttpMethod.Post, "settlement_transactions.php", form, ct, "ListSettlementTransactions").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<ExpressPayTransactionListResponse>(responseBody);
            if (envelope?.Transactions is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
                return Array.Empty<SettlementTransaction>();
            }

            var txns = new List<SettlementTransaction>(envelope.Transactions.Count);
            foreach (var d in envelope.Transactions)
                txns.Add(ToTransaction(d));

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return txns;
        }
        catch (Exception ex)
        {
            activity.SetOutcome(ClassifyOutcome(ex));
            throw;
        }
    }

    private Settlement ToSettlement(ExpressPaySettlementData d) => new()
    {
        Reference = d.SettlementId ?? string.Empty,
        NetAmount = d.NetAmount,
        GrossAmount = d.GrossAmount == 0 ? null : d.GrossAmount,
        Fees = d.Fees == 0 ? null : d.Fees,
        Currency = d.Currency ?? _options.Currency,
        SettledAt = d.SettledAt ?? d.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = d.BankAccount,
        TransactionCount = d.TransactionCount
    };

    private SettlementTransaction ToTransaction(ExpressPayTransactionData d) => new()
    {
        GatewayReference = d.Token ?? d.OrderId ?? string.Empty,
        Kind = MapKind(d.Type, d.Amount),
        NetAmount = d.NetAmount == 0 ? d.Amount : d.NetAmount,
        GrossAmount = d.Amount == 0 ? null : d.Amount,
        Fee = d.Fee == 0 ? null : d.Fee,
        Currency = d.Currency ?? _options.Currency,
        CreatedAt = d.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? type, decimal amount) =>
        type?.ToLowerInvariant() switch
        {
            "refund" or "refunded" => SettlementTransactionKind.Refund,
            "chargeback" => SettlementTransactionKind.Chargeback,
            "fee" => SettlementTransactionKind.Fee,
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

    // === ExpressPay API shapes (internal) ===

    private sealed class ExpressPaySettlementListResponse
    {
        [JsonPropertyName("settlements")] public List<ExpressPaySettlementData>? Settlements { get; set; }
    }

    private sealed class ExpressPaySettlementSingleResponse
    {
        [JsonPropertyName("settlement")] public ExpressPaySettlementData? Settlement { get; set; }
    }

    private sealed class ExpressPaySettlementData
    {
        [JsonPropertyName("settlement-id")] public string? SettlementId { get; set; }
        [JsonPropertyName("net-amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("gross-amount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("fees")] public decimal Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled-at")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("created-at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("bank-account")] public string? BankAccount { get; set; }
        [JsonPropertyName("transaction-count")] public int TransactionCount { get; set; }
    }

    private sealed class ExpressPayTransactionListResponse
    {
        [JsonPropertyName("transactions")] public List<ExpressPayTransactionData>? Transactions { get; set; }
    }

    private sealed class ExpressPayTransactionData
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("order-id")] public string? OrderId { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("net-amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fee")] public decimal Fee { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("created-at")] public DateTime? CreatedAt { get; set; }
    }
}
