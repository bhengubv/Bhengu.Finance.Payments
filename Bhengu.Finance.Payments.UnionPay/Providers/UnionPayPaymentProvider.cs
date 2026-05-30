// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.UnionPay.Providers;

/// <summary>
/// China UnionPay (UPOP) gateway 5.1 provider. The consumer-pay path is a redirect-form
/// gateway — ProcessPaymentAsync returns the action URL plus signed form parameters so the
/// caller can render the redirect (or build it server-side). Status query, refund and
/// back-notify verification use the same RSA-SHA256 form-signing scheme.
/// UnionPay does not expose a standard payout API; <see cref="IPayoutProvider"/> is not implemented.
/// </summary>
public sealed class UnionPayPaymentProvider : IPaymentGatewayProvider
{
    private const string FrontTransPath = "/gateway/api/frontTransReq.do";
    private const string QueryPath = "/gateway/api/queryTrans.do";
    private const string BackTransPath = "/gateway/api/backTransReq.do";

    private readonly HttpClient _httpClient;
    private readonly UnionPayOptions _options;
    private readonly ILogger<UnionPayPaymentProvider> _logger;
    private readonly string _baseUrl;

    public string ProviderName => "unionpay";

    public UnionPayPaymentProvider(
        HttpClient httpClient,
        IOptions<UnionPayOptions> options,
        ILogger<UnionPayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(UnionPayOptions.MerId)} is required");
        if (string.IsNullOrWhiteSpace(_options.CertId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(UnionPayOptions.CertId)} is required");
        if (string.IsNullOrWhiteSpace(_options.SignCertPrivateKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(UnionPayOptions.SignCertPrivateKey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://gateway.test.95516.com")
            : (_options.BaseUrl ?? "https://gateway.95516.com");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl, UriKind.Absolute);
    }

    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderId = request.PaymentMethodToken;
        var txnAmt = ((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        var txnTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        var fields = new Dictionary<string, string>
        {
            ["version"] = "5.1.0",
            ["encoding"] = _options.Encoding,
            ["certId"] = _options.CertId,
            ["signMethod"] = "01",
            ["txnType"] = "01",
            ["txnSubType"] = "01",
            ["bizType"] = "000201",
            ["channelType"] = "07",
            ["accessType"] = "0",
            ["merId"] = _options.MerId,
            ["orderId"] = orderId,
            ["txnTime"] = txnTime,
            ["txnAmt"] = txnAmt,
            ["currencyCode"] = _options.Currency,
            ["frontUrl"] = _options.FrontUrl,
            ["backUrl"] = _options.BackUrl
        };

        SignFields(fields);

        var actionUrl = _baseUrl + FrontTransPath;
        var body = BuildFormBody(fields);

        _logger.LogInformation("UnionPay frontTransReq prepared: orderId={OrderId} txnAmt={TxnAmt}", orderId, txnAmt);

        // Returns the redirect-form payload as Message; the caller renders or HTTP-302's the user.
        return Task.FromResult(new PaymentResponse
        {
            GatewayReference = orderId,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = _options.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = $"{actionUrl}?{body}"
        });
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderId = $"RF{DateTime.UtcNow:yyyyMMddHHmmssfff}"[..Math.Min(32, 17)];
        var txnAmt = ((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        var txnTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

        var fields = new Dictionary<string, string>
        {
            ["version"] = "5.1.0",
            ["encoding"] = _options.Encoding,
            ["certId"] = _options.CertId,
            ["signMethod"] = "01",
            ["txnType"] = "04",
            ["txnSubType"] = "00",
            ["bizType"] = "000201",
            ["accessType"] = "0",
            ["channelType"] = "07",
            ["merId"] = _options.MerId,
            ["orderId"] = orderId,
            ["origQryId"] = request.GatewayReference,
            ["txnTime"] = txnTime,
            ["txnAmt"] = txnAmt,
            ["backUrl"] = _options.BackUrl
        };

        SignFields(fields);

        var responseFields = await PostFormAsync(BackTransPath, fields, ct, "ProcessRefund").ConfigureAwait(false);
        var respCode = responseFields.GetValueOrDefault("respCode", "??");
        var queryId = responseFields.GetValueOrDefault("queryId", orderId);

        _logger.LogInformation("UnionPay refund response: respCode={RespCode} queryId={QueryId}", respCode, queryId);

        return new RefundResponse
        {
            GatewayReference = queryId,
            Amount = request.Amount,
            Status = MapRespCode(respCode, refundContext: true),
            ProcessedAt = DateTime.UtcNow,
            Message = responseFields.GetValueOrDefault("respMsg")
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.VerifyCertPublicKey))
        {
            _logger.LogWarning("UnionPay VerifyCertPublicKey not configured — webhook signature verification cannot succeed.");
            return false;
        }

        try
        {
            // UnionPay back-notify posts URL-encoded form fields including a `signature` field.
            // The signature is RSA-SHA256 over SHA-256(canonical_sorted_form_excluding_signature).
            var fields = ParseFormBody(payload);
            fields.Remove("signature");
            var canonical = BuildCanonical(fields);
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            var digestHex = Convert.ToHexString(digest).ToLowerInvariant();
            var digestBytes = Encoding.UTF8.GetBytes(digestHex);
            var sigBytes = Convert.FromBase64String(signature);

            using var rsa = LoadPublicKey(_options.VerifyCertPublicKey);
            return rsa.VerifyData(digestBytes, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UnionPay webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var fields = ParseFormBody(payload);
            if (fields.Count == 0)
                return Task.FromResult<WebhookEvent?>(null);

            var queryId = fields.GetValueOrDefault("queryId");
            var orderId = fields.GetValueOrDefault("orderId");
            var reference = !string.IsNullOrEmpty(queryId) ? queryId : orderId;
            var respCode = fields.GetValueOrDefault("respCode");
            var txnType = fields.GetValueOrDefault("txnType");

            if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(respCode))
                return Task.FromResult<WebhookEvent?>(null);

            var status = txnType switch
            {
                "04" => MapRespCode(respCode, refundContext: true),
                _ => MapRespCode(respCode)
            };

            _logger.LogInformation("Parsed UnionPay back-notify: txnType={TxnType} respCode={RespCode}", txnType, respCode);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = status,
                EventType = txnType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse UnionPay back-notify");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // === Internal helpers ===

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

    /// <summary>
    /// Append the <c>signature</c> field to <paramref name="fields"/> using UnionPay 5.1 rules:
    /// sort by key, concat as k=v&amp;k=v, SHA-256 hex, RSA-SHA256 over the hex string, base64.
    /// </summary>
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

    private static RSA LoadPublicKey(string pem)
    {
        var trimmed = StripPemHeaders(pem);
        var keyBytes = Convert.FromBase64String(trimmed);

        try
        {
            var cert = X509CertificateLoader.LoadCertificate(keyBytes);
            var rsa = cert.GetRSAPublicKey();
            if (rsa is not null) return rsa;
        }
        catch (CryptographicException) { /* not a cert; fall through */ }

        var bare = RSA.Create();
        try
        {
            bare.ImportSubjectPublicKeyInfo(keyBytes, out _);
        }
        catch (CryptographicException)
        {
            bare.ImportRSAPublicKey(keyBytes, out _);
        }
        return bare;
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

    private static PaymentStatus MapRespCode(string? respCode, bool refundContext = false) => respCode switch
    {
        "00" => refundContext ? PaymentStatus.Refunded : PaymentStatus.Completed,
        "03" or "04" or "05" => PaymentStatus.Pending, // queued / pending settlement
        "34" or "11" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Failed
    };
}
