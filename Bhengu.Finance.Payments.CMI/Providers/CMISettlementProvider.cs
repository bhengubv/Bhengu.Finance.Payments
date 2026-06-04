// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.CMI.Providers;

/// <summary>
/// CMI implementation of <see cref="ISettlementProvider"/> backed by CMI's CC5 daily-settlement
/// reporting endpoint. Each <c>EndOfDay</c> ledger entry becomes a <see cref="Settlement"/>; the
/// constituent transaction list is returned by <see cref="ListTransactionsAsync"/>.
/// </summary>
/// <remarks>
/// CMI does not publish an industrial settlement REST API — the merchant portal CSV download is
/// the canonical surface. This adapter wraps the CC5 <c>OrderHistory</c> /
/// <c>SettlementsReport</c> XML endpoint exposed for partner integrations.
/// </remarks>
public sealed class CMISettlementProvider : ISettlementProvider
{
    private readonly HttpClient _httpClient;
    private readonly CMIOptions _options;
    private readonly ILogger<CMISettlementProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.CMI;

    /// <summary>Construct a settlement provider. Designed to be registered via DI.</summary>
    public CMISettlementProvider(
        HttpClient httpClient,
        IOptions<CMIOptions> options,
        ILogger<CMISettlementProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CMIOptions.ClientId)} is required");

        CMIHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.list");
        var xml = BuildSettlementsReportXml(fromUtc, toUtc);
        var body = await CMIHttpClient.SendFormAsync(_httpClient, _logger, "fim/api",
            new Dictionary<string, string> { ["DATA"] = xml }, "ListSettlements", ct).ConfigureAwait(false);

        var doc = TryParseXml(body);
        if (doc?.Root is null) return Array.Empty<Settlement>();

        var list = new List<Settlement>();
        foreach (var s in doc.Root.Elements("Settlement"))
        {
            list.Add(new Settlement
            {
                Reference = s.Element("Id")?.Value ?? string.Empty,
                NetAmount = ParseDecimal(s.Element("NetAmount")?.Value),
                GrossAmount = TryParseNullableDecimal(s.Element("GrossAmount")?.Value),
                Fees = TryParseNullableDecimal(s.Element("Fees")?.Value),
                Currency = s.Element("Currency")?.Value ?? _options.Currency,
                SettledAt = ParseDate(s.Element("SettledAt")?.Value),
                BankAccountReference = s.Element("BankAccount")?.Value,
                TransactionCount = int.TryParse(s.Element("Count")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0
            });
        }
        return list;
    }

    /// <inheritdoc/>
    public async Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.get");

        var xml = BuildSettlementInquiryXml(settlementReference);
        try
        {
            var body = await CMIHttpClient.SendFormAsync(_httpClient, _logger, "fim/api",
                new Dictionary<string, string> { ["DATA"] = xml }, "GetSettlement", ct).ConfigureAwait(false);
            var doc = TryParseXml(body);
            var s = doc?.Root?.Element("Settlement");
            if (s is null) return null;

            return new Settlement
            {
                Reference = s.Element("Id")?.Value ?? settlementReference,
                NetAmount = ParseDecimal(s.Element("NetAmount")?.Value),
                GrossAmount = TryParseNullableDecimal(s.Element("GrossAmount")?.Value),
                Fees = TryParseNullableDecimal(s.Element("Fees")?.Value),
                Currency = s.Element("Currency")?.Value ?? _options.Currency,
                SettledAt = ParseDate(s.Element("SettledAt")?.Value),
                BankAccountReference = s.Element("BankAccount")?.Value,
                TransactionCount = int.TryParse(s.Element("Count")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "settlement.transactions");
        var xml = BuildSettlementTransactionsXml(settlementReference);
        var body = await CMIHttpClient.SendFormAsync(_httpClient, _logger, "fim/api",
            new Dictionary<string, string> { ["DATA"] = xml }, "SettlementTransactions", ct).ConfigureAwait(false);

        var doc = TryParseXml(body);
        if (doc?.Root is null) return Array.Empty<SettlementTransaction>();

        var list = new List<SettlementTransaction>();
        foreach (var t in doc.Root.Elements("Transaction"))
        {
            list.Add(new SettlementTransaction
            {
                GatewayReference = t.Element("OrderId")?.Value ?? string.Empty,
                Kind = MapKind(t.Element("Type")?.Value),
                NetAmount = ParseDecimal(t.Element("NetAmount")?.Value ?? t.Element("Total")?.Value),
                GrossAmount = TryParseNullableDecimal(t.Element("GrossAmount")?.Value),
                Fee = TryParseNullableDecimal(t.Element("Fee")?.Value),
                Currency = t.Element("Currency")?.Value ?? _options.Currency,
                CreatedAt = ParseDate(t.Element("Date")?.Value)
            });
        }
        return list;
    }

    private string BuildSettlementsReportXml(DateTime fromUtc, DateTime toUtc) =>
        new XDocument(
            new XDeclaration("1.0", "ISO-8859-9", null),
            new XElement("CC5Request",
                new XElement("Name", _options.ApiUser),
                new XElement("Password", _options.ApiPassword),
                new XElement("ClientId", _options.ClientId),
                new XElement("Type", "SettlementsReport"),
                new XElement("Extra",
                    new XElement("DateFrom", fromUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    new XElement("DateTo", toUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))))).ToString();

    private string BuildSettlementInquiryXml(string id) =>
        new XDocument(
            new XDeclaration("1.0", "ISO-8859-9", null),
            new XElement("CC5Request",
                new XElement("Name", _options.ApiUser),
                new XElement("Password", _options.ApiPassword),
                new XElement("ClientId", _options.ClientId),
                new XElement("Type", "SettlementInquiry"),
                new XElement("Extra",
                    new XElement("SettlementId", id)))).ToString();

    private string BuildSettlementTransactionsXml(string id) =>
        new XDocument(
            new XDeclaration("1.0", "ISO-8859-9", null),
            new XElement("CC5Request",
                new XElement("Name", _options.ApiUser),
                new XElement("Password", _options.ApiPassword),
                new XElement("ClientId", _options.ClientId),
                new XElement("Type", "SettlementTransactions"),
                new XElement("Extra",
                    new XElement("SettlementId", id)))).ToString();

    private XDocument? TryParseXml(string body)
    {
        try
        {
            return XDocument.Parse(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CMI settlement response could not be parsed: {Body}", body);
            return null;
        }
    }

    private static decimal ParseDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : 0m;

    private static decimal? TryParseNullableDecimal(string? value)
        => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static DateTime ParseDate(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : DateTime.UtcNow;

    private static SettlementTransactionKind MapKind(string? type) => type?.ToLowerInvariant() switch
    {
        "auth" or "preauth" or "capture" or "sale" => SettlementTransactionKind.Charge,
        "credit" or "refund" => SettlementTransactionKind.Refund,
        "chargeback" => SettlementTransactionKind.Chargeback,
        "fee" => SettlementTransactionKind.Fee,
        "adjustment" => SettlementTransactionKind.Adjustment,
        _ => SettlementTransactionKind.Other
    };
}
