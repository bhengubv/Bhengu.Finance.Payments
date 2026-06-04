// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.UnionPay.Providers;

/// <summary>
/// China UnionPay settlement / reconciliation provider. Wraps SecurePay's
/// <c>fileTransfer.do</c> + <c>queryTrans.do</c> endpoints used to fetch daily merchant
/// settlement files and individual settlement batches.
/// </summary>
/// <remarks>
/// UnionPay's settlement model returns daily merchant settlement files containing CSV-like
/// batches of completed transactions. This SDK exposes them via the SDK's normalised
/// <see cref="ISettlementProvider"/> shape — production deployments typically pair this with
/// scheduled cron jobs that pull the previous day's file and reconcile against their ledger.
/// </remarks>
public sealed class UnionPaySettlementProvider : ISettlementProvider
{
    private const string FileTransferPath = "/gateway/api/fileTransfer.do";
    private const string QueryPath = "/gateway/api/queryTrans.do";

    private readonly HttpClient _httpClient;
    private readonly UnionPayOptions _options;
    private readonly ILogger<UnionPaySettlementProvider> _logger;
    private readonly string _baseUrl;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.UnionPay;

    /// <summary>Create a new UnionPay settlement provider.</summary>
    public UnionPaySettlementProvider(
        HttpClient httpClient,
        IOptions<UnionPayOptions> options,
        ILogger<UnionPaySettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(UnionPayOptions.MerId)} is required");
        if (string.IsNullOrWhiteSpace(_options.SignCertPrivateKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(UnionPayOptions.SignCertPrivateKey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://gateway.test.95516.com")
            : (_options.BaseUrl ?? "https://gateway.95516.com");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl, UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list");
        try
        {
            var settlements = new List<Settlement>();
            for (var d = fromUtc.Date; d <= toUtc.Date; d = d.AddDays(1))
            {
                var settlementDate = d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                var fields = new Dictionary<string, string>
                {
                    ["version"] = "5.1.0",
                    ["encoding"] = _options.Encoding,
                    ["certId"] = _options.CertId,
                    ["signMethod"] = "01",
                    ["txnType"] = "76",
                    ["txnSubType"] = "01",
                    ["bizType"] = "000000",
                    ["accessType"] = "0",
                    ["merId"] = _options.MerId,
                    ["txnTime"] = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                    ["settleDate"] = settlementDate,
                    ["fileType"] = "00"
                };

                SignFields(fields);
                Dictionary<string, string> responseFields;
                try
                {
                    responseFields = await PostFormAsync(FileTransferPath, fields, ct, "ListSettlements").ConfigureAwait(false);
                }
                catch (PaymentDeclinedException)
                {
                    continue; // no settlement file for this date
                }

                var respCode = responseFields.GetValueOrDefault("respCode", "??");
                if (respCode != "00") continue;

                var settlementId = responseFields.GetValueOrDefault("batchNo", $"settle-{settlementDate}");
                var netAmtMinor = long.TryParse(responseFields.GetValueOrDefault("settleAmt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var net) ? net : 0L;
                var grossAmtMinor = long.TryParse(responseFields.GetValueOrDefault("totalAmt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gross) ? gross : (long?)null;
                var txnCount = int.TryParse(responseFields.GetValueOrDefault("totalQty"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) ? qty : 0;

                settlements.Add(new Settlement
                {
                    Reference = settlementId,
                    NetAmount = netAmtMinor / 100m,
                    GrossAmount = grossAmtMinor / 100m,
                    Fees = grossAmtMinor.HasValue ? (grossAmtMinor.Value - netAmtMinor) / 100m : (decimal?)null,
                    Currency = _options.Currency,
                    SettledAt = d,
                    BankAccountReference = responseFields.GetValueOrDefault("settleAcct"),
                    TransactionCount = txnCount
                });
            }

            _logger.LogInformation("UnionPay listed {Count} settlements between {From:o} and {To:o}",
                settlements.Count, fromUtc, toUtc);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return settlements;
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
            // UnionPay's settlement reference is the batch number; treat it as a YYYYMMDD-derived id
            // for the trailing 8 digits (other formats fall back to today).
            var date = TryParseSettlementDate(settlementReference) ?? DateTime.UtcNow.Date;
            var list = await ListSettlementsAsync(date, date, ct).ConfigureAwait(false);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return list.FirstOrDefault(s => s.Reference.Equals(settlementReference, StringComparison.Ordinal));
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
            var fields = new Dictionary<string, string>
            {
                ["version"] = "5.1.0",
                ["encoding"] = _options.Encoding,
                ["certId"] = _options.CertId,
                ["signMethod"] = "01",
                ["txnType"] = "76",
                ["txnSubType"] = "02",
                ["bizType"] = "000000",
                ["accessType"] = "0",
                ["merId"] = _options.MerId,
                ["batchNo"] = settlementReference,
                ["txnTime"] = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            };

            SignFields(fields);
            var responseFields = await PostFormAsync(QueryPath, fields, ct, "ListSettlementTransactions").ConfigureAwait(false);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            // UnionPay returns CSV-encoded line items in the `breakDownInfo` field. We split on
            // either "\n" or "|" and treat the first 6 columns as gatewayRef, type, amount, fee,
            // currency, createdAt (yyyyMMddHHmmss).
            var raw = responseFields.GetValueOrDefault("breakDownInfo");
            if (string.IsNullOrWhiteSpace(raw))
                return Array.Empty<SettlementTransaction>();

            var lines = raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            var list = new List<SettlementTransaction>(lines.Length);
            foreach (var line in lines)
            {
                var cols = line.Split(',');
                if (cols.Length < 5) continue;

                var amt = long.TryParse(cols[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) ? a : 0L;
                var fee = cols.Length > 3 && long.TryParse(cols[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0L;
                var createdAt = cols.Length > 5 && DateTime.TryParseExact(cols[5], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
                    ? dt
                    : DateTime.UtcNow;

                list.Add(new SettlementTransaction
                {
                    GatewayReference = cols[0],
                    Kind = MapKind(cols[1]),
                    NetAmount = (amt - fee) / 100m,
                    GrossAmount = amt / 100m,
                    Fee = fee / 100m,
                    Currency = cols.Length > 4 ? cols[4] : _options.Currency,
                    CreatedAt = createdAt
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

    private static SettlementTransactionKind MapKind(string? raw) => raw?.ToLowerInvariant() switch
    {
        "01" or "payment" or "charge" or "capture" => SettlementTransactionKind.Charge,
        "04" or "refund" => SettlementTransactionKind.Refund,
        "chargeback" or "dispute" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Other
    };

    private static DateTime? TryParseSettlementDate(string reference)
    {
        if (reference.Length < 8) return null;
        var tail = reference[^8..];
        return DateTime.TryParseExact(tail, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date.Date
            : null;
    }

    private async Task<Dictionary<string, string>> PostFormAsync(string path, Dictionary<string, string> fields, CancellationToken ct, string operation)
    {
        var body = BuildFormBody(fields);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded") { CharSet = "UTF-8" };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to UnionPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("UnionPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return ParseFormBody(responseBody);
    }

    private void SignFields(Dictionary<string, string> fields)
    {
        var canonical = BuildCanonical(fields);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var digestHex = Convert.ToHexString(digest).ToLowerInvariant();
        var digestBytes = Encoding.UTF8.GetBytes(digestHex);

        using var rsa = LoadPrivateKey(_options.SignCertPrivateKey);
        var signature = rsa.SignData(digestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        fields["signature"] = Convert.ToBase64String(signature);
    }

    private static string BuildCanonical(IDictionary<string, string> fields)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in fields.Where(k => !string.IsNullOrEmpty(k.Value)).OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append('&');
            sb.Append(kv.Key).Append('=').Append(kv.Value);
            first = false;
        }
        return sb.ToString();
    }

    private static string BuildFormBody(IDictionary<string, string> fields)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in fields)
        {
            if (!first) sb.Append('&');
            sb.Append(WebUtility.UrlEncode(kv.Key))
              .Append('=')
              .Append(WebUtility.UrlEncode(kv.Value));
            first = false;
        }
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseFormBody(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body)) return result;
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            var key = WebUtility.UrlDecode(pair[..idx]);
            var value = WebUtility.UrlDecode(pair[(idx + 1)..]);
            result[key] = value;
        }
        return result;
    }

    private static RSA LoadPrivateKey(string pem)
    {
        var rsa = RSA.Create();
        var trimmed = StripPemHeaders(pem);
        var keyBytes = Convert.FromBase64String(trimmed);
        try
        {
            rsa.ImportPkcs8PrivateKey(keyBytes, out _);
        }
        catch (CryptographicException)
        {
            rsa.ImportRSAPrivateKey(keyBytes, out _);
        }
        return rsa;
    }

    private static string StripPemHeaders(string pem)
    {
        var sb = new StringBuilder(pem.Length);
        foreach (var line in pem.Split('\n', '\r'))
        {
            var l = line.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("-----", StringComparison.Ordinal)) continue;
            sb.Append(l);
        }
        return sb.ToString();
    }
}
