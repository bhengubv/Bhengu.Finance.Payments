// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.WeChatPay.Providers;

/// <summary>
/// WeChat Pay QR-code provider — wraps WeChat Pay v3 Native order creation
/// (<c>POST /v3/pay/transactions/native</c>) and order query
/// (<c>GET /v3/pay/transactions/out-trade-no/{out_trade_no}</c>).
/// <para>
/// The returned <c>code_url</c> is a <c>weixin://wxpay/...</c> URL — the payer scans the
/// rendered QR with the WeChat app to authorise payment.
/// </para>
/// <para>
/// Authentication uses the WeChat Pay v3 schema: <c>WECHATPAY2-SHA256-RSA2048</c>
/// Authorization header with an RSA-SHA256 signature over
/// <c>METHOD\nURL\nTIMESTAMP\nNONCE\nBODY\n</c>, signed with the merchant API private key.
/// </para>
/// <para>
/// PNG and SVG output are NOT supported by this provider — request <see cref="QrFormat.Payload"/>
/// and render the returned <c>code_url</c> yourself using a library such as QRCoder. This keeps
/// the SDK dependency-free.
/// </para>
/// </summary>
public sealed class WeChatPayQrCodeProvider : IQrCodeProvider
{
    private const string NativePath = "/v3/pay/transactions/native";
    private const string QueryPathTemplate = "/v3/pay/transactions/out-trade-no/{0}?mchid={1}";

    private readonly HttpClient _httpClient;
    private readonly WeChatPayOptions _options;
    private readonly ILogger<WeChatPayQrCodeProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.WeChatPay;

    /// <summary>Construct a WeChat Pay QR provider.</summary>
    public WeChatPayQrCodeProvider(
        HttpClient httpClient,
        IOptions<WeChatPayOptions> options,
        ILogger<WeChatPayQrCodeProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.AppId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(WeChatPayOptions.AppId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(WeChatPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantPrivateKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(WeChatPayOptions.MerchantPrivateKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantCertSerialNo))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(WeChatPayOptions.MerchantCertSerialNo)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mch.weixin.qq.com");
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
                "WeChat Pay Native QR requires a locked amount; static QR codes (Amount=null) are not supported by /v3/pay/transactions/native.");
        }

        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? _options.Currency
            : request.Currency.ToUpperInvariant();
        // WeChat uses fen (1/100 CNY) as integer.
        var totalFen = (int)Math.Round(request.Amount.Value * 100m, MidpointRounding.AwayFromZero);

        var body = new Dictionary<string, object?>
        {
            ["appid"] = _options.AppId,
            ["mchid"] = _options.MerchantId,
            ["description"] = request.Description,
            ["out_trade_no"] = request.MerchantReference,
            ["notify_url"] = _options.NotifyUrl,
            ["amount"] = new { total = totalFen, currency }
        };

        if (request.ExpiresAt is { } exp)
        {
            // WeChat v3 expects RFC 3339 with timezone offset, e.g. "2026-08-06T15:05:00+08:00"
            // (Asia/Shanghai). We forward the caller's UTC time and convert to +08:00.
            var shanghai = exp.Kind == DateTimeKind.Utc
                ? new DateTimeOffset(exp, TimeSpan.Zero).ToOffset(TimeSpan.FromHours(8))
                : new DateTimeOffset(exp.ToUniversalTime(), TimeSpan.Zero).ToOffset(TimeSpan.FromHours(8));
            body["time_expire"] = shanghai.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
            _logger.LogDebug("WeChat Pay Native IdempotencyKey={Key} (out_trade_no is the natural dedupe key)", request.IdempotencyKey);

        var responseBody = await SendAsync(HttpMethod.Post, NativePath, body, ct, "GenerateQr").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WeChatPayNativeResponse>(responseBody);

        if (string.IsNullOrEmpty(parsed?.CodeUrl))
        {
            throw new BhenguPaymentException(
                ProviderName,
                "WeChat Pay Native call returned no code_url.");
        }

        _logger.LogInformation(
            "WeChat Pay QR generated: out_trade_no={OutTradeNo} code_url={CodeUrl}",
            request.MerchantReference,
            parsed.CodeUrl);

        return new QrCode
        {
            Reference = request.MerchantReference,
            Format = QrFormat.Payload,
            Payload = parsed.CodeUrl,
            Amount = request.Amount,
            Currency = currency,
            ExpiresAt = request.ExpiresAt
        };
    }

    /// <inheritdoc />
    public async Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrReference);

        var path = string.Format(
            CultureInfo.InvariantCulture,
            QueryPathTemplate,
            Uri.EscapeDataString(qrReference),
            Uri.EscapeDataString(_options.MerchantId));

        var responseBody = await SendAsync(HttpMethod.Get, path, body: null, ct, "GetQrStatus").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WeChatPayQueryResponse>(responseBody);

        return MapTradeState(parsed?.TradeState);
    }

    // === Internal helpers ===

    private async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        string operation)
    {
        // For GET we sign over an empty body per WeChat v3 spec.
        var json = body is null ? string.Empty : JsonSerializer.Serialize(body);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var signature = SignRequest(method.Method, path, timestamp, nonce, json, _options.MerchantPrivateKey);

        var authValue = $"mchid=\"{_options.MerchantId}\"," +
                        $"nonce_str=\"{nonce}\"," +
                        $"signature=\"{signature}\"," +
                        $"timestamp=\"{timestamp}\"," +
                        $"serial_no=\"{_options.MerchantCertSerialNo}\"";

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("WECHATPAY2-SHA256-RSA2048", authValue);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to WeChat Pay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "WeChat Pay {Operation} failed: {StatusCode} {Body}",
                operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string SignRequest(
        string method,
        string path,
        string timestamp,
        string nonce,
        string body,
        string privateKeyPem)
    {
        var canonical = $"{method}\n{path}\n{timestamp}\n{nonce}\n{body}\n";
        using var rsa = LoadPrivateKey(privateKeyPem);
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(signature);
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

    private static PaymentStatus MapTradeState(string? state) => state?.ToUpperInvariant() switch
    {
        "SUCCESS" => PaymentStatus.Completed,
        "NOTPAY" or "USERPAYING" or "ACCEPT" => PaymentStatus.Pending,
        "CLOSED" or "REVOKED" => PaymentStatus.Cancelled,
        "REFUND" => PaymentStatus.Refunded,
        "PAYERROR" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    // === WeChat Pay v3 response shapes (internal) ===

    private sealed class WeChatPayNativeResponse
    {
        [JsonPropertyName("code_url")] public string? CodeUrl { get; set; }
    }

    private sealed class WeChatPayQueryResponse
    {
        [JsonPropertyName("appid")] public string? AppId { get; set; }
        [JsonPropertyName("mchid")] public string? MchId { get; set; }
        [JsonPropertyName("out_trade_no")] public string? OutTradeNo { get; set; }
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("trade_state")] public string? TradeState { get; set; }
        [JsonPropertyName("trade_state_desc")] public string? TradeStateDesc { get; set; }
    }
}
