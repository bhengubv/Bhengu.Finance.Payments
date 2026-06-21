// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OPay.Providers;

/// <summary>
/// OPay implementation of <see cref="ISettlementProvider"/>. Wraps OPay's settlement reporting
/// endpoints — daily batches the acquirer has paid out to the merchant's nominated NUBAN.
/// </summary>
/// <remarks>
/// OPay returns wire amounts in the smallest currency unit (kobo for NGN, piastre for EGP);
/// the SDK divides by 100 before returning.
/// <para>UNVERIFIED: the <c>api/v1/international/settlement/*</c> paths are not in OPay's public
/// Cashier docs (https://documentation.opaycheckout.com/). Signing follows the documented
/// HMAC-SHA512 scheme, but the endpoints/payloads are unverified — treat as untested.</para>
/// </remarks>
public sealed class OPaySettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private readonly OPayHttpClient _http;
    private readonly OPayOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.OPay;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public OPaySettlementProvider(
        HttpClient httpClient,
        IOptions<OPayOptions> options,
        ILogger<OPaySettlementProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.SecretKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.MerchantId)} is required");

        _http = new OPayHttpClient(httpClient, _options, Logger);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new
        {
            publicKey = _options.PublicKey,
            sn = _options.MerchantId,
            startDate = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            pageSize = 100,
            pageNo = 1
        };
        var json = await RunOperationAsync("list_settlements",
            () => _http.SendAsync(HttpMethod.Post,
                "api/v1/international/settlement/list", body, "ListSettlements", ct), ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPaySettlementListData>>(json, OPayHttpClient.Json);
        if (resp?.Data?.Settlements is null) yield break;

        foreach (var s in resp.Data.Settlements)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSettlement(s);
        }
    }

    /// <inheritdoc />
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return RunOperationAsync<Settlement?>("get_settlement", async () =>
        {
            try
            {
                var body = new { publicKey = _options.PublicKey, sn = _options.MerchantId, settlementId = settlementReference };
                var json = await _http.SendAsync(HttpMethod.Post,
                    "api/v1/international/settlement/query", body, "GetSettlement", ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<OPayResponse<OPaySettlementData>>(json, OPayHttpClient.Json);
                return resp?.Data is null ? null : MapSettlement(resp.Data);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(
        string settlementReference,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        var body = new
        {
            publicKey = _options.PublicKey,
            sn = _options.MerchantId,
            settlementId = settlementReference,
            pageSize = 100,
            pageNo = 1
        };
        var json = await RunOperationAsync("list_settlement_transactions",
            () => _http.SendAsync(HttpMethod.Post,
                "api/v1/international/settlement/transactions", body, "ListSettlementTransactions", ct), ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayResponse<OPaySettlementTransactionListData>>(json, OPayHttpClient.Json);
        if (resp?.Data?.Transactions is null) yield break;

        foreach (var t in resp.Data.Transactions)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapTransaction(t);
        }
    }

    private static Settlement MapSettlement(OPaySettlementData s) => new()
    {
        Reference = s.SettlementId ?? string.Empty,
        NetAmount = s.NetAmount / 100m,
        GrossAmount = s.GrossAmount / 100m,
        Fees = (s.GrossAmount - s.NetAmount) / 100m,
        Currency = s.Currency ?? "NGN",
        SettledAt = s.SettledAt ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccount,
        TransactionCount = s.TransactionCount
    };

    private static SettlementTransaction MapTransaction(OPaySettlementTransactionData t) => new()
    {
        GatewayReference = t.OrderNo ?? t.Reference ?? string.Empty,
        Kind = MapKind(t.Type),
        NetAmount = t.NetAmount / 100m,
        GrossAmount = t.GrossAmount / 100m,
        Fee = t.Fee / 100m,
        Currency = t.Currency ?? "NGN",
        CreatedAt = t.CreatedAt ?? DateTime.UtcNow
    };

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "refund" or "reversal" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" or "service_fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Charge
    };

    // === OPay API shapes (internal) ===

    private sealed class OPayResponse<T> where T : class
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class OPaySettlementListData
    {
        [JsonPropertyName("settlements")] public List<OPaySettlementData>? Settlements { get; set; }
    }

    private sealed class OPaySettlementData
    {
        [JsonPropertyName("settlementId")] public string? SettlementId { get; set; }
        [JsonPropertyName("grossAmount")] public long GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public long NetAmount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settledAt")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("bankAccount")] public string? BankAccount { get; set; }
        [JsonPropertyName("transactionCount")] public int TransactionCount { get; set; }
    }

    private sealed class OPaySettlementTransactionListData
    {
        [JsonPropertyName("transactions")] public List<OPaySettlementTransactionData>? Transactions { get; set; }
    }

    private sealed class OPaySettlementTransactionData
    {
        [JsonPropertyName("orderNo")] public string? OrderNo { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("grossAmount")] public long GrossAmount { get; set; }
        [JsonPropertyName("netAmount")] public long NetAmount { get; set; }
        [JsonPropertyName("fee")] public long Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}
