// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayUIndia.Providers;

/// <summary>
/// PayU India settlement / reconciliation provider. Wraps PayU India's <c>get_settlement_details</c>
/// + <c>get_settlement</c> commands exposed under <c>merchant/postservice.php</c>.
/// </summary>
/// <remarks>
/// PayU India typically settles to the merchant's primary bank account on T+1 (or instant
/// settlement when enabled). The settlement-details endpoint returns line items inside a single
/// settlement batch — used for ledger reconciliation.
/// </remarks>
public sealed class PayUIndiaSettlementProvider : ISettlementProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly PayUIndiaOptions _options;
    private readonly ILogger<PayUIndiaSettlementProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.PayUIndia;

    /// <summary>Create a new PayU India settlement provider bound to the supplied HTTP client and options.</summary>
    public PayUIndiaSettlementProvider(
        HttpClient httpClient,
        IOptions<PayUIndiaOptions> options,
        ILogger<PayUIndiaSettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Salt))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.Salt)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.InfoBaseUrl ?? "https://info.payu.in/");
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list");
        try
        {
            const string command = "get_settlement_details";
            var fromStr = fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var toStr = toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var hashInput = string.Join("|", _options.MerchantKey, command, fromStr, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = fromStr,
                ["var2"] = toStr,
                ["hash"] = hash
            };

            var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "ListSettlements").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaSettlementListResponse>(raw, DeserializeOptions);

            _logger.LogInformation("PayU India listed {Count} settlements between {From:o} and {To:o}",
                response?.Settlements?.Count ?? 0, fromUtc, toUtc);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            var list = new List<Settlement>();
            if (response?.Settlements is null) return list;

            foreach (var s in response.Settlements)
                list.Add(MapSettlement(s));
            return list;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.get");
        try
        {
            const string command = "get_settlement";
            var hashInput = string.Join("|", _options.MerchantKey, command, settlementReference, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = settlementReference,
                ["hash"] = hash
            };

            var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "GetSettlement").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaSettlementResponse>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            if (response is null || string.IsNullOrEmpty(response.SettlementId))
                return null;

            return MapSettlement(response);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settlementReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.transactions");
        try
        {
            const string command = "get_settlement_breakup";
            var hashInput = string.Join("|", _options.MerchantKey, command, settlementReference, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = settlementReference,
                ["hash"] = hash
            };

            var raw = await PostFormAsync("merchant/postservice.php?form=2", form, ct, "ListTransactions").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaSettlementTransactionListResponse>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            var list = new List<SettlementTransaction>();
            if (response?.Transactions is null) return list;

            foreach (var t in response.Transactions)
            {
                list.Add(new SettlementTransaction
                {
                    GatewayReference = t.MihPayId ?? t.TxnId ?? string.Empty,
                    Kind = MapKind(t.Type),
                    NetAmount = t.NetAmount ?? 0m,
                    GrossAmount = t.GrossAmount,
                    Fee = t.Fee,
                    Currency = t.Currency ?? "INR",
                    CreatedAt = t.CreatedAt ?? DateTime.UtcNow
                });
            }
            return list;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private static Settlement MapSettlement(PayUIndiaSettlementResponse s) => new()
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

    private async Task<string> PostFormAsync(string path, IDictionary<string, string> form, CancellationToken ct, string operation)
    {
        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PayU India failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayU India {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed class PayUIndiaSettlementListResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("settlements")] public List<PayUIndiaSettlementResponse>? Settlements { get; set; }
    }

    private sealed class PayUIndiaSettlementResponse
    {
        [JsonPropertyName("settlement_id")] public string? SettlementId { get; set; }
        [JsonPropertyName("net_amount")] public decimal? NetAmount { get; set; }
        [JsonPropertyName("gross_amount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("fees")] public decimal? Fees { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("settled_at")] public DateTime? SettledAt { get; set; }
        [JsonPropertyName("bank_account_reference")] public string? BankAccountReference { get; set; }
        [JsonPropertyName("transaction_count")] public int? TransactionCount { get; set; }
    }

    private sealed class PayUIndiaSettlementTransactionListResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("transactions")] public List<PayUIndiaSettlementTransactionItem>? Transactions { get; set; }
    }

    private sealed class PayUIndiaSettlementTransactionItem
    {
        [JsonPropertyName("mihpayid")] public string? MihPayId { get; set; }
        [JsonPropertyName("txnid")] public string? TxnId { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("net_amount")] public decimal? NetAmount { get; set; }
        [JsonPropertyName("gross_amount")] public decimal? GrossAmount { get; set; }
        [JsonPropertyName("fee")] public decimal? Fee { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
    }
}
