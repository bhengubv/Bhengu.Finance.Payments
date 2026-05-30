// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.IPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.IPay.Providers;

/// <summary>
/// iPay (Kenya / Africa) payment gateway provider. Wraps iPay v3.
/// ProcessPaymentAsync constructs a hosted-payment-page redirect URL: the merchant builds
/// the URL with HMAC-SHA256 hex hash of concatenated fields and the customer is redirected.
/// The returned PaymentResponse carries the redirect URL in <see cref="PaymentResponse.Message"/>
/// and the merchant order id (oid) in <see cref="PaymentResponse.GatewayReference"/>.
/// iPay v3 has no refund API — refunds throw a configuration-style exception.
/// </summary>
public sealed class IPayPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly IPayOptions _options;
    private readonly ILogger<IPayPaymentProvider> _logger;

    public string ProviderName => ProviderNames.IPay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney;

    public IPayPaymentProvider(
        HttpClient httpClient,
        IOptions<IPayOptions> options,
        ILogger<IPayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.VendorId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(IPayOptions.VendorId)} is required");
        if (string.IsNullOrWhiteSpace(_options.HashKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(IPayOptions.HashKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://payments.ipayafrica.com/v3/ke"
                : _options.BaseUrl ?? "https://payments.ipayafrica.com/v3/ke";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var oid = request.PaymentMethodToken;
        var inv = request.Metadata?.GetValueOrDefault("inv") ?? oid;
        var ttl = request.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var tel = request.Metadata?.GetValueOrDefault("tel") ?? "";
        var eml = request.Metadata?.GetValueOrDefault("eml") ?? "";
        var vid = _options.VendorId;
        var curr = request.Currency.ToUpperInvariant();
        var p1 = request.Metadata?.GetValueOrDefault("p1") ?? "";
        var p2 = request.Metadata?.GetValueOrDefault("p2") ?? "";
        var p3 = request.Metadata?.GetValueOrDefault("p3") ?? "";
        var p4 = request.Metadata?.GetValueOrDefault("p4") ?? "";
        var cbk = _options.CallbackUrl;
        var cst = request.Metadata?.GetValueOrDefault("cst") ?? "1";
        var crl = request.Metadata?.GetValueOrDefault("crl") ?? "0";

        // iPay hash order: live + oid + inv + ttl + tel + eml + vid + curr + p1 + p2 + p3 + p4 + cbk + cst + crl
        var dataToHash = string.Concat(_options.Live, oid, inv, ttl, tel, eml, vid, curr, p1, p2, p3, p4, cbk, cst, crl);
        var hash = ComputeHmacHex(dataToHash, _options.HashKey);

        var pairs = new (string Key, string Value)[]
        {
            ("live", _options.Live), ("oid", oid), ("inv", inv), ("ttl", ttl),
            ("tel", tel), ("eml", eml), ("vid", vid), ("curr", curr),
            ("p1", p1), ("p2", p2), ("p3", p3), ("p4", p4),
            ("cbk", cbk), ("cst", cst), ("crl", crl), ("hash", hash)
        };
        var qs = string.Join('&', pairs.Select(p =>
            $"{WebUtility.UrlEncode(p.Key)}={WebUtility.UrlEncode(p.Value)}"));
        var redirectUrl = new Uri(_httpClient.BaseAddress!, "?" + qs).ToString();

        _logger.LogInformation("iPay redirect built for oid={Oid} amount={Amount}", oid, ttl);

        return Task.FromResult(new PaymentResponse
        {
            GatewayReference = oid,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = redirectUrl
        });
    }

    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "iPay v3 does not expose a refund API — issue a manual refund via the iPay merchant portal.",
            providerErrorCode: "not_supported");
    }

    /// <summary>
    /// Initiate a direct M-Pesa C2B charge via iPay's mobile SDK endpoint (api/sdk/v3/mpesa).
    /// Returns the iPay transaction status payload.
    /// </summary>
    public async Task<string> ChargeMpesaAsync(string phone, decimal amount, string oid, CancellationToken ct = default)
    {
        var ttl = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        var dataToHash = string.Concat(phone, _options.VendorId, ttl, oid);
        var hash = ComputeHmacHex(dataToHash, _options.HashKey);
        var body = new { phone, vid = _options.VendorId, amount = ttl, oid, hash };
        return await SendJsonAsync(HttpMethod.Post, "api/sdk/v3/mpesa", body, ct, "ChargeMpesa").ConfigureAwait(false);
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.HashKey))
        {
            _logger.LogWarning("iPay HashKey not configured — webhook verification cannot proceed.");
            return false;
        }

        try
        {
            // iPay's callback re-uses the same HMAC-SHA256-hex scheme over the concatenated
            // payload string. Caller must concatenate fields in the documented order before
            // invoking VerifyWebhookSignature.
            var expected = ComputeHmacHex(payload, _options.HashKey);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expected));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "iPay webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            // iPay callback can be application/x-www-form-urlencoded (txncd=..&status=..&...)
            // or JSON. Try JSON first; fall back to form-urlencoded.
            string? statusRaw, txncd, ipnid;
            if (payload.TrimStart().StartsWith('{'))
            {
                var json = JsonSerializer.Deserialize<IPayCallback>(payload);
                if (json is null) return Task.FromResult<WebhookEvent?>(null);
                statusRaw = json.Status;
                txncd = json.Txncd;
                ipnid = json.Ipnid;
            }
            else
            {
                var bag = ParseQueryString(payload);
                statusRaw = bag.GetValueOrDefault("status");
                txncd = bag.GetValueOrDefault("txncd");
                ipnid = bag.GetValueOrDefault("ipnid");
            }

            var status = statusRaw?.ToLowerInvariant() switch
            {
                "aei7p7yrx4ae34" => PaymentStatus.Completed, // iPay magic success code
                "bdi6p2yy76etrs" or "fe2707etr5s4wq" or "dtfi4p7yty45wq" => PaymentStatus.Failed,
                _ => (PaymentStatus?)null
            };

            var reference = txncd ?? ipnid;
            if (status is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = status.Value,
                EventType = statusRaw
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse iPay callback");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendJsonAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to iPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("iPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string ComputeHmacHex(string data, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static Dictionary<string, string> ParseQueryString(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = WebUtility.UrlDecode(pair[..eq]);
            var val = WebUtility.UrlDecode(pair[(eq + 1)..]);
            dict[key] = val;
        }
        return dict;
    }

    private sealed class IPayCallback
    {
        [JsonPropertyName("txncd")] public string? Txncd { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("ipnid")] public string? Ipnid { get; set; }
        [JsonPropertyName("mc")] public string? Mc { get; set; }
    }
}
