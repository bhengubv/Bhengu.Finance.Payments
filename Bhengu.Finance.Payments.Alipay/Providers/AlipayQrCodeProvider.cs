// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Alipay.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Alipay.Providers;

/// <summary>
/// Alipay QR-code provider — wraps the mainland Alipay OpenAPI
/// <c>alipay.trade.precreate</c> (merchant-presented QR for in-person and online
/// scan-to-pay) and <c>alipay.trade.query</c> (status polling) endpoints.
/// <para>
/// Signing uses RSA2 (<c>sha256WithRSA</c>) over the sorted-key canonical query string,
/// per the official Alipay docs. Reuses the merchant private key configured via
/// <see cref="AlipayOptions.MerchantPrivateKey"/>.
/// </para>
/// <para>
/// PNG and SVG output are NOT supported by this provider — request <see cref="QrFormat.Payload"/>
/// and render the returned <c>qr_code</c> string yourself using a library such as QRCoder.
/// This keeps the SDK dependency-free.
/// </para>
/// </summary>
public sealed class AlipayQrCodeProvider : IQrCodeProvider
{
    private const string PrecreateMethod = "alipay.trade.precreate";
    private const string QueryMethod = "alipay.trade.query";
    private const string SignType = "RSA2";
    private const string Format = "JSON";
    private const string Charset = "utf-8";
    private const string Version = "1.0";

    private readonly HttpClient _httpClient;
    private readonly AlipayOptions _options;
    private readonly ILogger<AlipayQrCodeProvider> _logger;
    private readonly string _gatewayUrl;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Alipay;

    /// <summary>Construct an Alipay QR provider.</summary>
    public AlipayQrCodeProvider(
        HttpClient httpClient,
        IOptions<AlipayOptions> options,
        ILogger<AlipayQrCodeProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AlipayOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantPrivateKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AlipayOptions.MerchantPrivateKey)} is required");

        _gatewayUrl = _options.OpenApiGatewayUrl
            ?? (_options.UseSandbox
                ? "https://openapi.alipaydev.com/gateway.do"
                : "https://openapi.alipay.com/gateway.do");
    }

    /// <inheritdoc />
    public async Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Format != QrFormat.Payload)
        {
            throw new BhenguPaymentException(
                ProviderName,
                $"QrFormat.{request.Format} not supported by this provider. Use QrFormat.Payload and render the payload yourself with QRCoder or similar.");
        }

        if (request.Amount is null)
        {
            throw new BhenguPaymentException(
                ProviderName,
                "Alipay alipay.trade.precreate requires a locked amount; static QR codes (Amount=null) are not supported by this API. Use alipay.fund.trans for a no-amount payee QR.");
        }

        var bizContent = new Dictionary<string, object?>
        {
            ["out_trade_no"] = request.MerchantReference,
            ["total_amount"] = request.Amount.Value.ToString("F2", CultureInfo.InvariantCulture),
            ["subject"] = request.Description
        };
        if (!string.IsNullOrWhiteSpace(request.PayerIdentifier))
            bizContent["buyer_id"] = request.PayerIdentifier;
        if (request.ExpiresAt is { } exp)
        {
            // Alipay accepts ISO timestamp 'yyyy-MM-dd HH:mm:ss' in Asia/Shanghai for time_expire.
            bizContent["time_expire"] = exp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        var json = await PostAsync(PrecreateMethod, bizContent, request.IdempotencyKey, ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AlipayPrecreateEnvelope>(json);

        var inner = parsed?.Response;
        if (inner is null || !string.Equals(inner.Code, "10000", StringComparison.Ordinal))
        {
            _logger.LogError(
                "Alipay precreate failed: code={Code} subCode={SubCode} msg={Msg} subMsg={SubMsg}",
                inner?.Code, inner?.SubCode, inner?.Msg, inner?.SubMsg);
            throw new BhenguPaymentException(
                ProviderName,
                "Alipay precreate rejected",
                providerErrorCode: inner?.SubCode ?? inner?.Code,
                providerErrorMessage: inner?.SubMsg ?? inner?.Msg);
        }

        if (string.IsNullOrEmpty(inner.QrCode))
        {
            throw new BhenguPaymentException(
                ProviderName,
                "Alipay precreate returned success but no qr_code field.");
        }

        var reference = string.IsNullOrEmpty(inner.OutTradeNo) ? request.MerchantReference : inner.OutTradeNo!;
        _logger.LogInformation("Alipay QR generated: out_trade_no={OutTradeNo}", reference);

        return new QrCode
        {
            Reference = reference,
            Format = QrFormat.Payload,
            Payload = inner.QrCode,
            Amount = request.Amount,
            Currency = request.Currency,
            ExpiresAt = request.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrReference);

        var bizContent = new Dictionary<string, object?>
        {
            ["out_trade_no"] = qrReference
        };

        var json = await PostAsync(QueryMethod, bizContent, idempotencyKey: null, ct).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AlipayQueryEnvelope>(json);
        var inner = parsed?.Response;

        if (inner is null)
        {
            throw new BhenguPaymentException(
                ProviderName,
                "Alipay query returned empty body.");
        }

        // Code 40004 / sub_code ACQ.TRADE_NOT_EXIST => not yet paid (treat as Pending).
        if (string.Equals(inner.Code, "40004", StringComparison.Ordinal)
            && string.Equals(inner.SubCode, "ACQ.TRADE_NOT_EXIST", StringComparison.OrdinalIgnoreCase))
        {
            return PaymentStatus.Pending;
        }

        if (!string.Equals(inner.Code, "10000", StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Alipay query non-success: code={Code} subCode={SubCode} msg={Msg}",
                inner.Code, inner.SubCode, inner.Msg);
            return PaymentStatus.Pending;
        }

        return MapTradeStatus(inner.TradeStatus);
    }

    private async Task<string> PostAsync(
        string method,
        IDictionary<string, object?> bizContent,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var bizContentJson = JsonSerializer.Serialize(bizContent, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        // Public params per Alipay OpenAPI v1.
        var publicParams = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["app_id"] = _options.ClientId,
            ["method"] = method,
            ["charset"] = Charset,
            ["sign_type"] = SignType,
            ["timestamp"] = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["version"] = Version,
            ["format"] = Format,
            ["biz_content"] = bizContentJson
        };

        if (!string.IsNullOrWhiteSpace(_options.NotifyUrl))
            publicParams["notify_url"] = _options.NotifyUrl;

        // Idempotency is honoured via out_trade_no (already inside biz_content) — Alipay treats repeated
        // out_trade_no submissions as the same logical order. We forward the SDK idempotency key for trace
        // visibility on the merchant side, but Alipay doesn't surface it as a request header.
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            _logger.LogDebug("Alipay {Method} IdempotencyKey={Key}", method, idempotencyKey);

        var canonical = BuildCanonicalString(publicParams);
        var sig = SignRsa2(canonical, _options.MerchantPrivateKey);
        publicParams["sign"] = sig;

        var form = new FormUrlEncodedContent(publicParams!);
        using var req = new HttpRequestMessage(HttpMethod.Post, _gatewayUrl) { Content = form };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Alipay OpenAPI failed", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Alipay {Method} HTTP failed: {Status} {Body}", method, response.StatusCode, body);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        return body;
    }

    private static string BuildCanonicalString(SortedDictionary<string, string> parameters)
    {
        var sb = new StringBuilder(256);
        var first = true;
        foreach (var kv in parameters)
        {
            if (string.IsNullOrEmpty(kv.Value)) continue;
            if (!first) sb.Append('&');
            sb.Append(kv.Key).Append('=').Append(kv.Value);
            first = false;
        }
        return sb.ToString();
    }

    private static string SignRsa2(string canonical, string privateKeyPem)
    {
        using var rsa = LoadPrivateKey(privateKeyPem);
        var sig = rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(sig);
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

    private static PaymentStatus MapTradeStatus(string? tradeStatus) => tradeStatus?.ToUpperInvariant() switch
    {
        "TRADE_SUCCESS" or "TRADE_FINISHED" => PaymentStatus.Completed,
        "WAIT_BUYER_PAY" => PaymentStatus.Pending,
        "TRADE_CLOSED" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // === Alipay OpenAPI envelopes ===

    private sealed class AlipayPrecreateEnvelope
    {
        [JsonPropertyName("alipay_trade_precreate_response")] public AlipayPrecreateResponse? Response { get; set; }
        [JsonPropertyName("sign")] public string? Sign { get; set; }
    }

    private sealed class AlipayPrecreateResponse
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("sub_code")] public string? SubCode { get; set; }
        [JsonPropertyName("sub_msg")] public string? SubMsg { get; set; }
        [JsonPropertyName("out_trade_no")] public string? OutTradeNo { get; set; }
        [JsonPropertyName("qr_code")] public string? QrCode { get; set; }
    }

    private sealed class AlipayQueryEnvelope
    {
        [JsonPropertyName("alipay_trade_query_response")] public AlipayQueryResponse? Response { get; set; }
        [JsonPropertyName("sign")] public string? Sign { get; set; }
    }

    private sealed class AlipayQueryResponse
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("msg")] public string? Msg { get; set; }
        [JsonPropertyName("sub_code")] public string? SubCode { get; set; }
        [JsonPropertyName("sub_msg")] public string? SubMsg { get; set; }
        [JsonPropertyName("out_trade_no")] public string? OutTradeNo { get; set; }
        [JsonPropertyName("trade_no")] public string? TradeNo { get; set; }
        [JsonPropertyName("trade_status")] public string? TradeStatus { get; set; }
    }
}
