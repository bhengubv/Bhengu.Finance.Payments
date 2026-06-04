// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.UnionPay.Providers;

/// <summary>
/// China UnionPay QuickPass (云闪付) merchant QR provider — wraps SecurePay's
/// <c>backTransReq.do</c> with <c>txnType=01</c> + <c>txnSubType=07</c> + <c>bizType=000000</c>
/// to issue a dynamic merchant-presented payment QR. Status is read via <c>queryTrans.do</c>.
/// </summary>
/// <remarks>
/// UnionPay QuickPass returns the raw <c>qrCode</c> string from <c>backTransReq</c>; the merchant
/// is responsible for rendering it as a QR image. Format is the QuickPass URL scheme
/// (<c>https://qr.95516.com/00010000/01...</c>) which any QR encoder converts to a scannable image.
/// </remarks>
public sealed class UnionPayQrCodeProvider : BhenguProviderBase, IQrCodeProvider
{
    private const string BackTransPath = "/gateway/api/backTransReq.do";
    private const string QueryPath = "/gateway/api/queryTrans.do";

    private readonly HttpClient _httpClient;
    private readonly UnionPayOptions _options;
    private readonly string _baseUrl;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.UnionPay;

    /// <summary>Create a new UnionPay QuickPass QR provider.</summary>
    public UnionPayQrCodeProvider(
        HttpClient httpClient,
        IOptions<UnionPayOptions> options,
        ILogger<UnionPayQrCodeProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

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
    public async Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "qr.generate");
        try
        {
            var orderId = request.MerchantReference;
            var txnAmt = ((long)Math.Round((request.Amount ?? 0m) * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
            var txnTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

            var fields = new Dictionary<string, string>
            {
                ["version"] = "5.1.0",
                ["encoding"] = _options.Encoding,
                ["certId"] = _options.CertId,
                ["signMethod"] = "01",
                ["txnType"] = "01",
                ["txnSubType"] = "07",
                ["bizType"] = "000000",
                ["channelType"] = "08",
                ["accessType"] = "0",
                ["merId"] = _options.MerId,
                ["orderId"] = orderId,
                ["txnTime"] = txnTime,
                ["txnAmt"] = txnAmt,
                ["currencyCode"] = _options.Currency,
                ["backUrl"] = _options.BackUrl
            };

            SignFields(fields);

            var responseFields = await PostFormAsync(BackTransPath, fields, ct, "GenerateQr").ConfigureAwait(false);
            var respCode = responseFields.GetValueOrDefault("respCode", "??");
            var qrCode = responseFields.GetValueOrDefault("qrCode");

            Logger.LogInformation("UnionPay QR backTransReq: respCode={RespCode} qrCode={HasQr}",
                respCode, !string.IsNullOrEmpty(qrCode));

            if (respCode != "00" || string.IsNullOrEmpty(qrCode))
                throw new BhenguPaymentException(ProviderName,
                    responseFields.GetValueOrDefault("respMsg") ?? $"UnionPay QR creation failed (respCode={respCode})",
                    providerErrorCode: respCode);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return new QrCode
            {
                Reference = orderId,
                Format = QrFormat.Payload,
                Payload = qrCode,
                Amount = request.Amount,
                Currency = request.Currency.ToUpperInvariant(),
                ExpiresAt = request.ExpiresAt
            };
        }
        catch (BhenguPaymentException)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Declined);
            throw;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qrReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "qr.status");
        try
        {
            var txnTime = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var fields = new Dictionary<string, string>
            {
                ["version"] = "5.1.0",
                ["encoding"] = _options.Encoding,
                ["certId"] = _options.CertId,
                ["signMethod"] = "01",
                ["txnType"] = "00",
                ["txnSubType"] = "00",
                ["bizType"] = "000000",
                ["accessType"] = "0",
                ["channelType"] = "08",
                ["merId"] = _options.MerId,
                ["orderId"] = qrReference,
                ["txnTime"] = txnTime
            };

            SignFields(fields);
            var responseFields = await PostFormAsync(QueryPath, fields, ct, "GetQrStatus").ConfigureAwait(false);

            var origRespCode = responseFields.GetValueOrDefault("origRespCode", string.Empty);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return MapRespCode(origRespCode);
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private static PaymentStatus MapRespCode(string? respCode) => respCode switch
    {
        "00" => PaymentStatus.Completed,
        "03" or "04" or "05" => PaymentStatus.Pending,
        "34" or "11" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

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
            Logger.LogError("UnionPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
