// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paymob.Providers;

/// <summary>
/// Paymob implementation of <see cref="ISettlementProvider"/>. Wraps Paymob's settlement-feed
/// endpoint (<c>/api/acceptance/settlements</c>) for per-day batch reconciliation.
/// </summary>
public sealed class PaymobSettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaymobOptions _options;
    private readonly ILogger<PaymobSettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Paymob;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public PaymobSettlementProvider(
        HttpClient httpClient,
        IOptions<PaymobOptions> options,
        ILogger<PaymobSettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaymobOptions.ApiKey)} is required");

        PaymobHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<PaymobSettlement>? items;
        using (BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list"))
        {
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, _logger, _options, ct).ConfigureAwait(false);
            var from = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get,
                $"api/acceptance/settlements?from={from}&to={to}&auth_token={Uri.EscapeDataString(authToken)}",
                null, "ListSettlements", ct).ConfigureAwait(false);

            var response = JsonSerializer.Deserialize<PaymobSettlementListResponse>(responseBody, PaymobHttpClient.Json);
            items = response?.Results;
        }

        if (items is null) yield break;
        foreach (var s in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSettlement(s);
        }
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.get");
        try
        {
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, _logger, _options, ct).ConfigureAwait(false);
            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get,
                $"api/acceptance/settlements/{Uri.EscapeDataString(settlementReference)}?auth_token={Uri.EscapeDataString(authToken)}",
                null, "GetSettlement", ct).ConfigureAwait(false);

            var settlement = JsonSerializer.Deserialize<PaymobSettlement>(responseBody, PaymobHttpClient.Json);
            return settlement is null ? null : MapSettlement(settlement);
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
        List<PaymobSettlementTxn>? items;
        using (BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.transactions"))
        {
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, _logger, _options, ct).ConfigureAwait(false);
            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get,
                $"api/acceptance/settlements/{Uri.EscapeDataString(settlementReference)}/transactions?auth_token={Uri.EscapeDataString(authToken)}",
                null, "SettlementTransactions", ct).ConfigureAwait(false);

            var response = JsonSerializer.Deserialize<PaymobSettlementTxnListResponse>(responseBody, PaymobHttpClient.Json);
            items = response?.Results;
        }

        if (items is null) yield break;
        foreach (var t in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(t);
        }
    }

    private static Settlement MapSettlement(PaymobSettlement s) => new()
    {
        Reference = s.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        NetAmount = s.NetAmountCents / 100m,
        GrossAmount = s.GrossAmountCents.HasValue ? s.GrossAmountCents.Value / 100m : null,
        Fees = s.FeeCents.HasValue ? s.FeeCents.Value / 100m : null,
        Currency = s.Currency ?? "EGP",
        SettledAt = s.SettledAt ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccountNumber,
        TransactionCount = s.TransactionCount ?? 0
    };

    private static SettlementTransaction MapTransaction(PaymobSettlementTxn t) => new()
    {
        GatewayReference = t.TransactionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        Kind = MapKind(t.Type),
        NetAmount = t.NetAmountCents / 100m,
        GrossAmount = t.GrossAmountCents.HasValue ? t.GrossAmountCents.Value / 100m : null,
        Fee = t.FeeCents.HasValue ? t.FeeCents.Value / 100m : null,
        Currency = t.Currency ?? "EGP",
        CreatedAt = t.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? type) => type?.ToLowerInvariant() switch
    {
        "charge" or "auth" or "capture" => SettlementTransactionKind.Charge,
        "refund" => SettlementTransactionKind.Refund,
        "chargeback" or "dispute" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Other
    };

    private sealed class PaymobSettlementListResponse
    {
        [JsonPropertyName("results")] public List<PaymobSettlement>? Results { get; set; }
    }

    private sealed class PaymobSettlement
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("net_amount_cents")] public long NetAmountCents { get; set; }
        [JsonPropertyName("gross_amount_cents")] public long? GrossAmountCents { get; set; }
        [JsonPropertyName("fee_cents")] public long? FeeCents { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled_at")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("bank_account_number")] public string? BankAccountNumber { get; set; }
        [JsonPropertyName("transaction_count")] public int? TransactionCount { get; set; }
    }

    private sealed class PaymobSettlementTxnListResponse
    {
        [JsonPropertyName("results")] public List<PaymobSettlementTxn>? Results { get; set; }
    }

    private sealed class PaymobSettlementTxn
    {
        [JsonPropertyName("transaction_id")] public long? TransactionId { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("net_amount_cents")] public long NetAmountCents { get; set; }
        [JsonPropertyName("gross_amount_cents")] public long? GrossAmountCents { get; set; }
        [JsonPropertyName("fee_cents")] public long? FeeCents { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}
