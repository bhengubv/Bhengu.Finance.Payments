// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.UnionPay.Providers;

/// <summary>
/// China UnionPay UPOP 3-D Secure provider. UnionPay's 3DS flow is the same browser-redirect
/// flow as <c>frontTransReq</c> but with <c>txnSubType=01</c> + <c>bizType=000201</c> escalated
/// through the issuer's ACS — the back-notify returns ECI/CAVV proof of the issuer's challenge
/// authentication outcome.
/// </summary>
/// <remarks>
/// The merchant signs the same canonical-sorted-form pattern as the parent provider:
/// SHA-256(canonical) → RSA-SHA256 → base64. Querying a 3DS authentication state uses
/// <c>queryTrans.do</c> with the same merId + orderId + txnTime triple.
/// </remarks>
public sealed class UnionPayThreeDSecureProvider : BhenguProviderBase, IThreeDSecureProvider
{
    private const string FrontTransPath = "/gateway/api/frontTransReq.do";
    private const string QueryPath = "/gateway/api/queryTrans.do";

    private readonly HttpClient _httpClient;
    private readonly UnionPayOptions _options;
    private readonly string _baseUrl;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.UnionPay;

    /// <summary>Create a new UnionPay 3DS provider.</summary>
    public UnionPayThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<UnionPayOptions> options,
        ILogger<UnionPayThreeDSecureProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

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

    /// <inheritdoc />
    public Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);
        return RunOperationAsync("start_3ds_authentication", () =>
        {
            var orderId = chargeIntent.PaymentMethodToken;
            var txnAmt = ((long)Math.Round(chargeIntent.Amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
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
                // 3DS — request explicit issuer authentication
                ["threeDSecure"] = "1",
                ["frontUrl"] = _options.FrontUrl,
                ["backUrl"] = _options.BackUrl
            };

            SignFields(fields);

            var actionUrl = _baseUrl + FrontTransPath;
            var body = BuildFormBody(fields);

            Logger.LogInformation("UnionPay UPOP 3DS challenge prepared: orderId={OrderId}", orderId);

            return Task.FromResult(new ThreeDSecureChallenge
            {
                Status = ThreeDSecureStatus.ChallengeRequired,
                ChallengeReference = orderId,
                RedirectUrl = $"{actionUrl}?{body}",
                ProtocolVersion = "2.2.0"
            });
        }, ct);
    }

    /// <inheritdoc />
    public Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeReference);
        return RunOperationAsync("get_3ds_challenge", async () =>
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
                ["bizType"] = "000201",
                ["accessType"] = "0",
                ["channelType"] = "07",
                ["merId"] = _options.MerId,
                ["orderId"] = challengeReference,
                ["txnTime"] = txnTime
            };

            SignFields(fields);
            var responseFields = await PostFormAsync(QueryPath, fields, ct, "GetChallenge").ConfigureAwait(false);

            var respCode = responseFields.GetValueOrDefault("respCode", "??");
            var origRespCode = responseFields.GetValueOrDefault("origRespCode", string.Empty);
            var eci = responseFields.GetValueOrDefault("eci");
            var cavv = responseFields.GetValueOrDefault("cavv") ?? responseFields.GetValueOrDefault("xid");
            var dsTransactionId = responseFields.GetValueOrDefault("dsTransactionId");

            Logger.LogInformation("UnionPay 3DS challenge query: respCode={RespCode} origRespCode={OrigRespCode} eci={Eci}",
                respCode, origRespCode, eci);

            var status = (respCode, origRespCode) switch
            {
                ("00", "00") => ThreeDSecureStatus.Authenticated,
                ("00", "03" or "04" or "05") => ThreeDSecureStatus.ChallengeRequired,
                ("00", _) => ThreeDSecureStatus.Attempted,
                _ => ThreeDSecureStatus.Failed
            };

            return new ThreeDSecureChallenge
            {
                Status = status,
                ChallengeReference = challengeReference,
                ChallengePayload = cavv,
                ProtocolVersion = "2.2.0",
                DsTransactionId = dsTransactionId
            };
        }, ct);
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
