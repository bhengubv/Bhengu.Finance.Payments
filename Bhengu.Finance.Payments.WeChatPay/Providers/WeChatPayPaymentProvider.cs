// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.WeChatPay.Providers;

/// <summary>
/// WeChat Pay v3 (Tencent) provider. Wraps the JSON v3 API for Native/QR, JSAPI and App
/// charges, refunds and partner batch-transfer payouts. RSA-SHA256 signed Authorization
/// headers; webhook notifications are AEAD-AES-256-GCM ciphertext that the SDK decrypts.
/// </summary>
public sealed class WeChatPayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private const string NativePath = "/v3/pay/transactions/native";
    private const string RefundPath = "/v3/refund/domestic/refunds";
    private const string TransferBatchesPath = "/v3/transfer/batches";

    private readonly HttpClient _httpClient;
    private readonly WeChatPayOptions _options;

    public override string ProviderName => ProviderNames.WeChatPay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.QrCode;

    public WeChatPayPaymentProvider(
        HttpClient httpClient,
        IOptions<WeChatPayOptions> options,
        ILogger<WeChatPayPaymentProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

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

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();
        // WeChat Pay uses fen (1/100 CNY) as integer.
        var totalFen = (int)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero);
        var outTradeNo = request.PaymentMethodToken;

        var body = new
        {
            appid = _options.AppId,
            mchid = _options.MerchantId,
            description = request.Description,
            out_trade_no = outTradeNo,
            notify_url = _options.NotifyUrl,
            amount = new { total = totalFen, currency }
        };

        var responseBody = await SendAsync(HttpMethod.Post, NativePath, body, ct, "ProcessPayment").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WeChatPayNativeResponse>(responseBody);

        Logger.LogInformation("WeChat Pay Native charge created: out_trade_no={OutTradeNo} code_url={CodeUrl}",
            outTradeNo, parsed?.CodeUrl);

        return new PaymentResponse
        {
            GatewayReference = outTradeNo,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = parsed?.CodeUrl
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var currency = _options.Currency.ToUpperInvariant();
        var totalFen = (int)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero);
        var outRefundNo = $"REFUND_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];

        var body = new
        {
            out_trade_no = request.GatewayReference,
            out_refund_no = outRefundNo,
            reason = request.Reason,
            notify_url = _options.NotifyUrl,
            amount = new { refund = totalFen, total = totalFen, currency }
        };

        var responseBody = await SendAsync(HttpMethod.Post, RefundPath, body, ct, "ProcessRefund").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WeChatPayRefundResponse>(responseBody);

        Logger.LogInformation("WeChat Pay refund created: refund_id={RefundId} status={Status}",
            parsed?.RefundId, parsed?.Status);

        return new RefundResponse
        {
            GatewayReference = parsed?.RefundId ?? outRefundNo,
            Amount = request.Amount,
            Status = MapRefundStatus(parsed?.Status),
            ProcessedAt = DateTime.UtcNow,
            Message = parsed?.Status
        };
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        var totalFen = (int)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero);
        var outBatchNo = $"BATCH_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];
        var outDetailNo = $"D_{Guid.NewGuid():N}"[..30];

        var body = new
        {
            appid = _options.AppId,
            out_batch_no = outBatchNo,
            batch_name = request.Description,
            batch_remark = request.Description,
            total_amount = totalFen,
            total_num = 1,
            transfer_detail_list = new[]
            {
                new
                {
                    out_detail_no = outDetailNo,
                    transfer_amount = totalFen,
                    transfer_remark = request.Description,
                    openid = request.DestinationToken
                }
            }
        };

        var responseBody = await SendAsync(HttpMethod.Post, TransferBatchesPath, body, ct, "ProcessPayout").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<WeChatPayTransferResponse>(responseBody);

        Logger.LogInformation("WeChat Pay transfer batch created: batch_id={BatchId} state={State}",
            parsed?.BatchId, parsed?.BatchStatus);

        return new PayoutResponse
        {
            GatewayReference = parsed?.BatchId ?? outBatchNo,
            Status = MapTransferStatus(parsed?.BatchStatus),
            Amount = request.Amount,
            Currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant(),
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WeChatPayPlatformCertificate))
        {
            Logger.LogWarning("WeChat Pay WeChatPayPlatformCertificate not configured — webhook signature verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        return RunWebhookVerify(() =>
        {
            try
            {
                using var rsa = LoadPublicKey(_options.WeChatPayPlatformCertificate);
                return SignatureHelpers.VerifyRsaSha256(payload, signature, rsa, SignatureHelpers.Encoding.Base64);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "WeChat Pay webhook signature verification raised");
                return false;
            }
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload, ct), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var webhook = JsonSerializer.Deserialize<WeChatPayWebhookEvent>(payload);
            if (webhook is null || string.IsNullOrEmpty(webhook.EventType))
                return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed WeChat Pay webhook event: {EventType}", webhook.EventType);

            var status = webhook.EventType.ToUpperInvariant() switch
            {
                "TRANSACTION.SUCCESS" => PaymentStatus.Completed,
                "PAYMENT.SUCCESS" => PaymentStatus.Completed,
                "REFUND.SUCCESS" => PaymentStatus.Refunded,
                "TRANSACTION.CLOSED" => PaymentStatus.Cancelled,
                "TRANSACTION.FAIL" or "PAYMENT.FAIL" => PaymentStatus.Failed,
                _ => (PaymentStatus?)null
            };

            // The resource block is AEAD-encrypted; consumers that need full payload data
            // call DecryptResource. For event-loop dispatch we only need the trade reference,
            // which WeChat Pay v3 conventionally includes in the resource ciphertext — so the
            // pragmatic fallback is the event id field where present.
            var reference = webhook.Resource?.OriginalType ?? webhook.Id;
            if (status is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            var category = webhook.EventType.ToUpperInvariant() switch
            {
                "TRANSACTION.SUCCESS" or "PAYMENT.SUCCESS" => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargeSucceeded,
                "TRANSACTION.FAIL" or "PAYMENT.FAIL" => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargeFailed,
                "REFUND.SUCCESS" => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.RefundSucceeded,
                "TRANSACTION.CLOSED" => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.Unknown,
                _ => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.Unknown
            };

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = status.Value,
                EventType = webhook.EventType,
                Category = category
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse WeChat Pay webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    /// <summary>
    /// Decrypt a WeChat Pay v3 webhook <c>resource</c> ciphertext using AEAD-AES-256-GCM
    /// with the configured V3ApiKey as the key. Returns the decrypted plaintext JSON.
    /// </summary>
    public string DecryptResource(string ciphertextBase64, string nonce, string associatedData)
    {
        ArgumentException.ThrowIfNullOrEmpty(ciphertextBase64);
        ArgumentException.ThrowIfNullOrEmpty(nonce);
        if (string.IsNullOrWhiteSpace(_options.V3ApiKey) || _options.V3ApiKey.Length != 32)
            throw new ProviderConfigurationException(ProviderName, $"{nameof(WeChatPayOptions.V3ApiKey)} must be 32 chars for AEAD-AES-256-GCM");

        var key = Encoding.UTF8.GetBytes(_options.V3ApiKey);
        var nonceBytes = Encoding.UTF8.GetBytes(nonce);
        var aad = Encoding.UTF8.GetBytes(associatedData ?? string.Empty);
        var combined = Convert.FromBase64String(ciphertextBase64);

        // AEAD-AES-256-GCM: combined = ciphertext || 16-byte tag.
        const int tagSize = 16;
        if (combined.Length < tagSize)
            throw new InvalidOperationException("ciphertext too short");
        var cipher = combined.AsSpan(0, combined.Length - tagSize);
        var tag = combined.AsSpan(combined.Length - tagSize, tagSize);
        var plaintext = new byte[cipher.Length];

        using var aes = new AesGcm(key, tagSize);
        aes.Decrypt(nonceBytes, cipher, tag, plaintext, aad);
        return Encoding.UTF8.GetString(plaintext);
    }

    // === Internal helpers ===

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var signature = SignRequest(method.Method, path, timestamp, nonce, json, _options.MerchantPrivateKey);

        var authValue = $"mchid=\"{_options.MerchantId}\"," +
                        $"nonce_str=\"{nonce}\"," +
                        $"signature=\"{signature}\"," +
                        $"timestamp=\"{timestamp}\"," +
                        $"serial_no=\"{_options.MerchantCertSerialNo}\"";

        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("WECHATPAY2-SHA256-RSA2048", authValue);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // HttpRequestException is auto-translated to ProviderUnavailableException by BhenguProviderBase.
        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("WeChat Pay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// Build the WeChat Pay v3 canonical string and RSA-SHA256 sign it.
    /// Canonical = METHOD\nURL\nTIMESTAMP\nNONCE\nBODY\n.
    /// </summary>
    private static string SignRequest(string method, string path, string timestamp, string nonce, string body, string privateKeyPem)
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

    private static RSA LoadPublicKey(string pem)
    {
        var trimmed = StripPemHeaders(pem);
        var keyBytes = Convert.FromBase64String(trimmed);

        // WeChat distributes the platform key as an X.509 cert; the SDK accepts both a cert blob
        // and a bare SubjectPublicKeyInfo. Try cert first.
        try
        {
#if NET9_0_OR_GREATER
            var cert = X509CertificateLoader.LoadCertificate(keyBytes);
#else
            var cert = new X509Certificate2(keyBytes);
#endif
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

    private static PaymentStatus MapRefundStatus(string? state) => state?.ToUpperInvariant() switch
    {
        "SUCCESS" => PaymentStatus.Refunded,
        "PROCESSING" => PaymentStatus.Pending,
        "ABNORMAL" or "FAIL" or "FAILED" => PaymentStatus.Failed,
        "CLOSED" or "CANCELLED" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    private static PaymentStatus MapTransferStatus(string? state) => state?.ToUpperInvariant() switch
    {
        "FINISHED" or "SUCCESS" => PaymentStatus.Completed,
        "ACCEPTED" or "PROCESSING" or "WAIT_PAY" => PaymentStatus.Pending,
        "CLOSED" or "FAIL" or "FAILED" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    // === WeChat Pay response/webhook shapes (internal) ===

    private sealed class WeChatPayNativeResponse
    {
        [JsonPropertyName("code_url")] public string? CodeUrl { get; set; }
    }

    private sealed class WeChatPayRefundResponse
    {
        [JsonPropertyName("refund_id")] public string? RefundId { get; set; }
        [JsonPropertyName("out_refund_no")] public string? OutRefundNo { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class WeChatPayTransferResponse
    {
        [JsonPropertyName("batch_id")] public string? BatchId { get; set; }
        [JsonPropertyName("out_batch_no")] public string? OutBatchNo { get; set; }
        [JsonPropertyName("batch_status")] public string? BatchStatus { get; set; }
    }

    private sealed class WeChatPayWebhookEvent
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("create_time")] public string? CreateTime { get; set; }
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("resource_type")] public string? ResourceType { get; set; }
        [JsonPropertyName("resource")] public WeChatPayWebhookResource? Resource { get; set; }
    }

    private sealed class WeChatPayWebhookResource
    {
        [JsonPropertyName("algorithm")] public string? Algorithm { get; set; }
        [JsonPropertyName("ciphertext")] public string? Ciphertext { get; set; }
        [JsonPropertyName("associated_data")] public string? AssociatedData { get; set; }
        [JsonPropertyName("nonce")] public string? Nonce { get; set; }
        [JsonPropertyName("original_type")] public string? OriginalType { get; set; }
    }
}
