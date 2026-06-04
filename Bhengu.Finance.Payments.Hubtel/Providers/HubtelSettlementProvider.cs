// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Hubtel.Providers;

/// <summary>
/// Hubtel settlement provider — wraps the Hubtel reconciliation feed.
/// <para>
/// Hubtel surfaces settlements via <c>/merchantaccount/merchants/{posSalesNumber}/statement</c> and
/// constituent transactions via <c>/transactions/{posSalesNumber}/history</c> with date filters.
/// </para>
/// </summary>
public sealed class HubtelSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly HubtelOptions _options;
    private readonly ILogger<HubtelSettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Hubtel;

    /// <summary>Construct the Hubtel settlement provider. Designed to be registered via DI.</summary>
    public HubtelSettlementProvider(
        HttpClient httpClient,
        IOptions<HubtelOptions> options,
        ILogger<HubtelSettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantAccountNumber))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.MerchantAccountNumber)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-txnstatus.hubtel.com/"
                : _options.BaseUrl ?? "https://api-txnstatus.hubtel.com/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<HubtelStatementData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list"))
        {
            try
            {
                var path = $"merchantaccount/merchants/{Uri.EscapeDataString(_options.MerchantAccountNumber)}/statement?from={fromUtc:yyyy-MM-dd}&to={toUtc:yyyy-MM-dd}";
                var responseBody = await HubtelHttpClient.SendAsync(_httpClient, _logger, HttpMethod.Get, path, body: null, ct, "ListSettlements").ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<HubtelStatementResponse>(responseBody);
                items = envelope?.Data;

                _logger.LogInformation("Hubtel settlements listed: {Count} between {From:O} and {To:O}", items?.Count ?? 0, fromUtc, toUtc);
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
            var path = $"merchantaccount/merchants/{Uri.EscapeDataString(_options.MerchantAccountNumber)}/statement/{Uri.EscapeDataString(settlementReference)}";
            var responseBody = await HubtelHttpClient.SendAsync(_httpClient, _logger, HttpMethod.Get, path, body: null, ct, "GetSettlement").ConfigureAwait(false);
            var envelope = JsonSerializer.Deserialize<HubtelStatementSingleResponse>(responseBody);
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

        List<HubtelTransactionData>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list_transactions"))
        {
            try
            {
                var path = $"transactions/{Uri.EscapeDataString(_options.MerchantAccountNumber)}/history?statementId={Uri.EscapeDataString(settlementReference)}";
                var responseBody = await HubtelHttpClient.SendAsync(_httpClient, _logger, HttpMethod.Get, path, body: null, ct, "ListSettlementTransactions").ConfigureAwait(false);
                var envelope = JsonSerializer.Deserialize<HubtelTransactionListResponse>(responseBody);
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

    private Settlement ToSettlement(HubtelStatementData d) => new()
    {
        Reference = d.StatementId ?? string.Empty,
        NetAmount = d.NetAmount,
        GrossAmount = d.GrossAmount == 0 ? null : d.GrossAmount,
        Fees = d.Charges == 0 ? null : d.Charges,
        Currency = d.Currency ?? _options.Currency,
        SettledAt = d.SettledAt ?? d.CreatedAt ?? DateTime.UtcNow,
        BankAccountReference = d.SettlementAccount,
        TransactionCount = d.TransactionCount
    };

    private SettlementTransaction ToTransaction(HubtelTransactionData d) => new()
    {
        GatewayReference = d.TransactionId ?? d.ClientReference ?? string.Empty,
        Kind = MapKind(d.TransactionType, d.Amount),
        NetAmount = d.AmountAfterCharges == 0 ? d.Amount : d.AmountAfterCharges,
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

    // === Hubtel API shapes (internal) ===

    private sealed class HubtelStatementResponse
    {
        [JsonPropertyName("data")] public List<HubtelStatementData>? Data { get; set; }
    }

    private sealed class HubtelStatementSingleResponse
    {
        [JsonPropertyName("data")] public HubtelStatementData? Data { get; set; }
    }

    private sealed class HubtelStatementData
    {
        [JsonPropertyName("statementId")] public string? StatementId { get; set; }
        [JsonPropertyName("netAmount")] public decimal NetAmount { get; set; }
        [JsonPropertyName("grossAmount")] public decimal GrossAmount { get; set; }
        [JsonPropertyName("charges")] public decimal Charges { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settledAt")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("settlementAccount")] public string? SettlementAccount { get; set; }
        [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
    }

    private sealed class HubtelTransactionListResponse
    {
        [JsonPropertyName("data")] public List<HubtelTransactionData>? Data { get; set; }
    }

    private sealed class HubtelTransactionData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
        [JsonPropertyName("transactionType")] public string? TransactionType { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("amountAfterCharges")] public decimal AmountAfterCharges { get; set; }
        [JsonPropertyName("charges")] public decimal Charges { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}
