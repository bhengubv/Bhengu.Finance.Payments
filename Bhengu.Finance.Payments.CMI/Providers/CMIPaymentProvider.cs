// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.CMI.Providers;

/// <summary>
/// CMI (Centre Monetique Interbancaire / Morocco) 3D Secure payment gateway provider.
/// CMI is a redirect-only 3DS card gateway based on the Garanti BBVA POS XML protocol.
/// ProcessPaymentAsync returns a redirect URL in <see cref="PaymentResponse.Message"/> that
/// the caller must navigate the payer to. There is no payouts API — CMI does not implement
/// <see cref="IPayoutProvider"/>.
/// </summary>
public sealed class CMIPaymentProvider : IPaymentGatewayProvider
{
    private const string LiveDefaultUrl = "https://payment.cmi.co.ma/";
    private const string SandboxDefaultUrl = "https://testpayment.cmi.co.ma/";

    private readonly HttpClient _httpClient;
    private readonly CMIOptions _options;
    private readonly ILogger<CMIPaymentProvider> _logger;

    public string ProviderName => "cmi";

    public CMIPaymentProvider(
        HttpClient httpClient,
        IOptions<CMIOptions> options,
        ILogger<CMIPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CMIOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.StoreKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CMIOptions.StoreKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var url = _options.UseSandbox
                ? _options.SandboxUrl ?? SandboxDefaultUrl
                : _options.BaseUrl ?? LiveDefaultUrl;
            _httpClient.BaseAddress = new Uri(url);
        }
    }

    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // PaymentMethodToken is the merchant's order id (oid). CMI does not have card tokens —
        // the caller is responsible for the (oid, amount, email) tuple.
        var orderId = string.IsNullOrWhiteSpace(request.PaymentMethodToken)
            ? $"cmi-{Guid.NewGuid():N}"
            : request.PaymentMethodToken;
        var amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : NormaliseCurrency(request.Currency);
        var email = request.Metadata?.GetValueOrDefault("email") ?? string.Empty;
        var billToName = request.Metadata?.GetValueOrDefault("BillToName") ?? string.Empty;
        var rnd = request.Metadata?.GetValueOrDefault("rnd") ?? DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

        // Field order is documented by CMI/Garanti BBVA. We follow hashAlgorithm=ver3 which
        // hashes all POSTed fields (sorted, pipe-joined) plus the storekey, then SHA-512 base64.
        var fields = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["clientid"] = _options.ClientId,
            ["storetype"] = "3D_PAY_HOSTING",
            ["hashAlgorithm"] = "ver3",
            ["TranType"] = "PreAuth",
            ["amount"] = amount,
            ["currency"] = currency,
            ["oid"] = orderId,
            ["okUrl"] = _options.OkUrl ?? string.Empty,
            ["failUrl"] = _options.FailUrl ?? string.Empty,
            ["lang"] = _options.Lang,
            ["callbackUrl"] = _options.CallbackUrl ?? string.Empty,
            ["refreshtime"] = "5",
            ["rnd"] = rnd,
            ["BillToName"] = billToName,
            ["email"] = email
        };

        var hash = ComputeRedirectHash(fields, _options.StoreKey);
        fields["hash"] = hash;

        var baseUrl = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? (_options.UseSandbox ? SandboxDefaultUrl : LiveDefaultUrl);
        var sb = new StringBuilder();
        sb.Append(baseUrl).Append("/fim/est3Dgate?");
        var first = true;
        foreach (var kv in fields)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
        }

        var redirectUrl = sb.ToString();

        _logger.LogInformation("CMI redirect URL built for oid={OrderId} amount={Amount} currency={Currency}",
            orderId, amount, currency);

        return Task.FromResult(new PaymentResponse
        {
            GatewayReference = orderId,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            Message = redirectUrl
        });
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var total = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var xml = BuildCC5Request("Credit", request.GatewayReference, total);

        var body = await SendXmlAsync("fim/api", xml, ct, "ProcessRefund").ConfigureAwait(false);
        var parsed = ParseCC5Response(body);

        _logger.LogInformation("CMI refund for oid={OrderId} response={Response} procReturnCode={Code}",
            request.GatewayReference, parsed.Response, parsed.ProcReturnCode);

        return new RefundResponse
        {
            GatewayReference = request.GatewayReference,
            Amount = request.Amount,
            Status = parsed.Response == "Approved" ? PaymentStatus.Refunded : PaymentStatus.Failed,
            ProcessedAt = DateTime.UtcNow,
            Message = parsed.Response
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.StoreKey))
        {
            _logger.LogWarning("CMI StoreKey not configured — callback hash verification cannot succeed.");
            return false;
        }

        try
        {
            // CMI callback payload arrives as a form-urlencoded body. We expect callers to pass the
            // already-parsed payload as a sorted "k=v&k=v" string (or anything that the StoreKey will
            // close out to the recomputed SHA-512 base64 digest CMI sends in the HASH field).
            var canonical = payload + _options.StoreKey;
            var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(canonical));
            var computed = Convert.ToBase64String(bytes);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(computed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CMI callback hash verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var pairs = ParseFormUrlEncoded(payload);
            var oid = pairs.GetValueOrDefault("oid");
            var procReturnCode = pairs.GetValueOrDefault("ProcReturnCode") ?? pairs.GetValueOrDefault("procReturnCode");
            var response = pairs.GetValueOrDefault("Response") ?? pairs.GetValueOrDefault("response");
            var mdStatus = pairs.GetValueOrDefault("mdStatus");

            _logger.LogInformation("Parsed CMI callback: oid={Oid} response={Response} procReturn={Proc} mdStatus={MdStatus}",
                oid, response, procReturnCode, mdStatus);

            if (string.IsNullOrEmpty(oid))
                return Task.FromResult<WebhookEvent?>(null);

            // 3D Secure mdStatus: 1 = full auth, 2/3/4 = attempted, else failed.
            // procReturnCode = "00" means approved on the issuer side.
            var status = (response?.ToUpperInvariant(), procReturnCode) switch
            {
                ("APPROVED", "00") => PaymentStatus.Completed,
                ("APPROVED", _) => PaymentStatus.Completed,
                ("DECLINED", _) => PaymentStatus.Failed,
                ("ERROR", _) => PaymentStatus.Failed,
                _ when mdStatus == "1" => PaymentStatus.Completed,
                _ => (PaymentStatus?)null
            };

            if (status is null)
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = oid,
                Status = status.Value,
                EventType = response
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse CMI callback");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    internal static string ComputeRedirectHash(SortedDictionary<string, string> fields, string storeKey)
    {
        // hashAlgorithm=ver3: pipe-join all values in sorted-key order, escape pipes/backslashes
        // in values, then append storeKey, then SHA-512 base64.
        var sb = new StringBuilder();
        foreach (var kv in fields)
        {
            if (string.Equals(kv.Key, "hash", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kv.Key, "encoding", StringComparison.OrdinalIgnoreCase)) continue;
            var escaped = (kv.Value ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
            sb.Append(escaped).Append('|');
        }
        var escapedKey = (storeKey ?? string.Empty).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
        sb.Append(escapedKey);
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToBase64String(bytes);
    }

    private string BuildCC5Request(string type, string orderId, string total)
    {
        // System.Xml.Linq for safety — we never concatenate XML by hand.
        var doc = new XDocument(
            new XDeclaration("1.0", "ISO-8859-9", null),
            new XElement("CC5Request",
                new XElement("Name", _options.ApiUser),
                new XElement("Password", _options.ApiPassword),
                new XElement("ClientId", _options.ClientId),
                new XElement("OrderId", orderId),
                new XElement("Type", type),
                new XElement("Currency", _options.Currency),
                new XElement("Total", total)));
        return doc.ToString();
    }

    private (string? Response, string? ProcReturnCode, string? OrderId) ParseCC5Response(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var root = doc.Root;
            return (
                root?.Element("Response")?.Value,
                root?.Element("ProcReturnCode")?.Value,
                root?.Element("OrderId")?.Value
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CMI XML response could not be parsed: {Body}", body);
            return (null, null, null);
        }
    }

    private async Task<string> SendXmlAsync(string path, string xml, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(xml, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        // CMI's fim/api endpoint expects the XML body as form data under the key DATA.
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["DATA"] = xml });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to CMI failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("CMI {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static Dictionary<string, string> ParseFormUrlEncoded(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(body)) return dict;
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            dict[key] = value;
        }
        return dict;
    }

    private static string NormaliseCurrency(string currency)
    {
        // Accept either alpha-3 (MAD) or numeric (504). CMI requires numeric.
        return currency.ToUpperInvariant() switch
        {
            "MAD" => "504",
            "USD" => "840",
            "EUR" => "978",
            "GBP" => "826",
            _ => currency
        };
    }
}
