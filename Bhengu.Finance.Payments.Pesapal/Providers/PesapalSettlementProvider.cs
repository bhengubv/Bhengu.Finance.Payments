// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Pesapal.Providers;

/// <summary>
/// Pesapal settlement provider — wraps the <c>/api/Statements/</c> reconciliation endpoints.
/// </summary>
public sealed class PesapalSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly PesapalOptions _options;
    private readonly ILogger<PesapalSettlementProvider> _logger;
    private readonly PesapalTokenCache _tokenCache;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Pesapal;

    /// <summary>Construct the Pesapal settlement provider. Designed to be registered via DI.</summary>
    public PesapalSettlementProvider(
        HttpClient httpClient,
        IOptions<PesapalOptions> options,
        ILogger<PesapalSettlementProvider> logger,
        PesapalTokenCache? tokenCache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tokenCache = tokenCache ?? new PesapalTokenCache();

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? (_options.SandboxUrl ?? "https://cybqa.pesapal.com/pesapalv3")
                : (_options.BaseUrl ?? "https://pay.pesapal.com/v3");
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
            var path = $"api/Statements/GetStatement?startDate={fromUtc:yyyy-MM-dd}&endDate={toUtc:yyyy-MM-dd}";
            var token = await PesapalHttpClient.EnsureTokenAsync(_httpClient, _logger, _options, _tokenCache, ct).ConfigureAwait(false);
            var responseBody = await PesapalHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, path, body: null, token, ct, "ListSettlements").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<PesapalStatementListResponse>(responseBody);
            if (envelope?.Data is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
                return Array.Empty<Settlement>();
            }

            var settlements = new List<Settlement>(envelope.Data.Count);
            foreach (var d in envelope.Data)
                settlements.Add(ToSettlement(d));

            _logger.LogInformation("Pesapal statements listed: {Count} between {From:O} and {To:O}", settlements.Count, fromUtc, toUtc);
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
            var path = $"api/Statements/GetSettlement?settlementId={Uri.EscapeDataString(settlementReference)}";
            var token = await PesapalHttpClient.EnsureTokenAsync(_httpClient, _logger, _options, _tokenCache, ct).ConfigureAwait(false);
            var responseBody = await PesapalHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, path, body: null, token, ct, "GetSettlement").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<PesapalStatementSingleResponse>(responseBody);
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
    public async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list_transactions");
        try
        {
            var path = $"api/Statements/GetSettlementTransactions?settlementId={Uri.EscapeDataString(settlementReference)}";
            var token = await PesapalHttpClient.EnsureTokenAsync(_httpClient, _logger, _options, _tokenCache, ct).ConfigureAwait(false);
            var responseBody = await PesapalHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, path, body: null, token, ct, "ListSettlementTransactions").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<PesapalTransactionListResponse>(responseBody);
            if (envelope?.Data is null)
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
                return Array.Empty<SettlementTransaction>();
            }

            var txns = new List<SettlementTransaction>(envelope.Data.Count);
            foreach (var d in envelope.Data)
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

    private Settlement ToSettlement(PesapalStatementData d) => new()
    {
        Reference = d.SettlementId ?? string.Empty,
        NetAmount = d.NetAmount,
        GrossAmount = d.GrossAmount == 0 ? null : d.GrossAmount,
        Fees = d.Fees == 0 ? null : d.Fees,
        Currency = d.Currency ?? _options.Currency,
        SettledAt = d.SettlementDate ?? DateTime.UtcNow,
        BankAccountReference = d.BankAccount,
        TransactionCount = d.TransactionCount
    };

    private SettlementTransaction ToTransaction(PesapalTransactionData d) => new()
    {
        GatewayReference = d.ConfirmationCode ?? d.OrderTrackingId ?? string.Empty,
        Kind = MapKind(d.TransactionType, d.Amount),
        NetAmount = d.NetAmount == 0 ? d.Amount : d.NetAmount,
        GrossAmount = d.Amount == 0 ? null : d.Amount,
        Fee = d.Fees == 0 ? null : d.Fees,
        Currency = d.Currency ?? _options.Currency,
        CreatedAt = d.CreatedDate ?? DateTime.UtcNow
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

    private sealed class PesapalStatementListResponse
    {
        [JsonPropertyName("data")] public List<PesapalStatementData>? Data { get; set; }
    }

    private sealed class PesapalStatementSingleResponse
    {
        [JsonPropertyName("data")] public PesapalStatementData? Data { get; set; }
    }

    private sealed class PesapalStatementData
    {
        [JsonPropertyName("settlement_id")] public string? SettlementId { get; set; }
        [JsonPropertyName("net_amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("gross_amount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("fees")] public decimal Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settlement_date")] public DateTime? SettlementDate { get; set; }
        [JsonPropertyName("bank_account")] public string? BankAccount { get; set; }
        [JsonPropertyName("transaction_count")] public int TransactionCount { get; set; }
    }

    private sealed class PesapalTransactionListResponse
    {
        [JsonPropertyName("data")] public List<PesapalTransactionData>? Data { get; set; }
    }

    private sealed class PesapalTransactionData
    {
        [JsonPropertyName("confirmation_code")] public string? ConfirmationCode { get; set; }
        [JsonPropertyName("order_tracking_id")] public string? OrderTrackingId { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("net_amount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("fees")] public decimal Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("transaction_type")] public string? TransactionType { get; set; }
        [JsonPropertyName("created_date")] public DateTime? CreatedDate { get; set; }
    }
}
