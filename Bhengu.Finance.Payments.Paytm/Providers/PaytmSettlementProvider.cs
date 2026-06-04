// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paytm.Providers;

/// <summary>
/// Paytm settlement / reconciliation provider. Wraps Paytm's <c>settlement/info</c>,
/// <c>settlement/getSettlement</c>, and <c>settlement/getSettlementBreakup</c> endpoints
/// for daily settlement reconciliation.
/// </summary>
public sealed class PaytmSettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _httpClient;
    private readonly PaytmOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Paytm;

    /// <summary>Create a new Paytm settlement provider.</summary>
    public PaytmSettlementProvider(
        HttpClient httpClient,
        IOptions<PaytmOptions> options,
        ILogger<PaytmSettlementProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://securegw-stage.paytm.in/")
                : (_options.BaseUrl ?? "https://securegw.paytm.in/"));
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<PaytmSettlementBody>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list"))
        {
            try
            {
                var body = new
                {
                    mid = _options.MerchantId,
                    fromDate = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    toDate = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                };
                var signature = ComputeChecksum(JsonSerializer.Serialize(body, SerializeOptions));
                var envelope = new { body, head = new { signature } };

                var raw = await SendAsync(HttpMethod.Post, "settlement/info", envelope, ct, "ListSettlements").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<PaytmSettlementListEnvelope>(raw, DeserializeOptions);
                items = response?.Body?.Settlements;

                Logger.LogInformation("Paytm listed {Count} settlements between {From:o} and {To:o}", items?.Count ?? 0, fromUtc, toUtc);
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            }
            catch
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var s in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapSettlement(s);
        }
    }

    /// <inheritdoc />
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementReference);
        return RunOperationAsync<Settlement?>("get_settlement", async () =>
        {
            try
            {
                var body = new { mid = _options.MerchantId, settlementId = settlementReference };
                var signature = ComputeChecksum(JsonSerializer.Serialize(body, SerializeOptions));
                var envelope = new { body, head = new { signature } };

                var raw = await SendAsync(HttpMethod.Post, "settlement/getSettlement", envelope, ct, "GetSettlement").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<PaytmSettlementEnvelope>(raw, DeserializeOptions);

                return response?.Body?.SettlementId is null ? null : MapSettlement(response.Body);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(string settlementReference, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementReference);
        List<PaytmSettlementBreakupItem>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.transactions"))
        {
            try
            {
                var body = new { mid = _options.MerchantId, settlementId = settlementReference };
                var signature = ComputeChecksum(JsonSerializer.Serialize(body, SerializeOptions));
                var envelope = new { body, head = new { signature } };

                var raw = await SendAsync(HttpMethod.Post, "settlement/getSettlementBreakup", envelope, ct, "ListTransactions").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<PaytmSettlementBreakupEnvelope>(raw, DeserializeOptions);
                items = response?.Body?.Transactions;
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            }
            catch
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var t in items)
        {
            ct.ThrowIfCancellationRequested();
            yield return new SettlementTransaction
            {
                GatewayReference = t.OrderId ?? t.TxnId ?? string.Empty,
                Kind = MapKind(t.Type),
                NetAmount = t.NetAmount ?? 0m,
                GrossAmount = t.GrossAmount,
                Fee = t.Fee,
                Currency = t.Currency ?? "INR",
                CreatedAt = t.CreatedAt ?? DateTime.UtcNow
            };
        }
    }

    private static Settlement MapSettlement(PaytmSettlementBody s) => new()
    {
        Reference = s.SettlementId ?? string.Empty,
        NetAmount = s.NetAmount ?? 0m,
        GrossAmount = s.GrossAmount,
        Fees = s.Fees,
        Currency = s.Currency ?? "INR",
        SettledAt = s.SettledAt ?? DateTime.UtcNow,
        BankAccountReference = s.BankAccountReference,
        TransactionCount = s.TransactionCount ?? 0
    };

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "payment" or "charge" or "capture" => SettlementTransactionKind.Charge,
        "refund" => SettlementTransactionKind.Refund,
        "chargeback" or "dispute" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Other
    };

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, SerializeOptions);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Paytm failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Paytm {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private string ComputeChecksum(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MerchantKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private sealed class PaytmSettlementListEnvelope
    {
        [JsonPropertyName("body")] public PaytmSettlementListBody? Body { get; set; }
    }

    private sealed class PaytmSettlementListBody
    {
        [JsonPropertyName("settlements")] public List<PaytmSettlementBody>? Settlements { get; set; }
    }

    private sealed class PaytmSettlementEnvelope
    {
        [JsonPropertyName("body")] public PaytmSettlementBody? Body { get; set; }
    }

    private sealed class PaytmSettlementBody
    {
        [JsonPropertyName("settlementId")] public string? SettlementId { get; set; }
        [JsonPropertyName("netAmount")] public decimal? NetAmount { get; set; }
        [JsonPropertyName("grossAmount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("fees")] public decimal? Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settledAt")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("bankAccountReference")] public string? BankAccountReference { get; set; }
        [JsonPropertyName("transactionCount")] public int? TransactionCount { get; set; }
    }

    private sealed class PaytmSettlementBreakupEnvelope
    {
        [JsonPropertyName("body")] public PaytmSettlementBreakupBody? Body { get; set; }
    }

    private sealed class PaytmSettlementBreakupBody
    {
        [JsonPropertyName("transactions")] public List<PaytmSettlementBreakupItem>? Transactions { get; set; }
    }

    private sealed class PaytmSettlementBreakupItem
    {
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
        [JsonPropertyName("txnId")] public string? TxnId { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("netAmount")] public decimal? NetAmount { get; set; }
        [JsonPropertyName("grossAmount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("fee")] public decimal? Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    }
}
