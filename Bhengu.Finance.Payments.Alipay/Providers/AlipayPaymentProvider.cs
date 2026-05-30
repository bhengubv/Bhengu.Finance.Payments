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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Alipay.Providers;

/// <summary>
/// Alipay+ Cross-Border payment provider (Ant Group). Wraps the Alipay+ Open API for
/// global merchants accepting Chinese consumers (1B+ users). RSA-SHA256 signed requests,
/// response/webhook signature verification, and IPayoutProvider for disbursements.
/// </summary>
public sealed class AlipayPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private const string PayPath = "/ams/api/v1/payments/pay";
    private const string RefundPath = "/ams/api/v1/payments/refund";
    private const string PayoutPath = "/ams/api/v1/payments/payout";

    private readonly HttpClient _httpClient;
    private readonly AlipayOptions _options;
    private readonly ILogger<AlipayPaymentProvider> _logger;
    private readonly string _baseUrl;

    public string ProviderName => ProviderNames.Alipay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Cards;

    public AlipayPaymentProvider(
        HttpClient httpClient,
        IOptions<AlipayOptions> options,
        ILogger<AlipayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AlipayOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantPrivateKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AlipayOptions.MerchantPrivateKey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://open-global.alipay.com/api/sandbox")
            : (_options.BaseUrl ?? "https://open-global.alipay.com");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl, UriKind.Absolute);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();
        // Alipay+ uses minor units (cents) as a string.
        var minorUnits = ((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        var paymentRequestId = request.PaymentMethodToken;

        var body = new
        {
            productCode = "CASHIER_PAYMENT",
            paymentRequestId,
            order = new
            {
                orderAmount = new { currency, value = minorUnits },
                orderDescription = request.Description,
                merchantInfo = new
                {
                    merchantMCC = "0000",
                    merchantName = _options.ClientId,
                    referenceMerchantId = _options.ClientId
                }
            },
            paymentMethod = new { paymentMethodType = "ALIPAY_CN" },
            paymentAmount = new { currency, value = minorUnits },
            paymentNotifyUrl = _options.NotifyUrl,
            paymentRedirectUrl = _options.RedirectUrl,
            settlementStrategy = new { settlementCurrency = currency }
        };

        var responseBody = await SendAsync(PayPath, body, ct, "ProcessPayment").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AlipayPaymentResponse>(responseBody);

        _logger.LogInformation("Alipay pay created: paymentId={PaymentId} resultCode={ResultCode}",
            parsed?.PaymentId, parsed?.Result?.ResultCode);

        return new PaymentResponse
        {
            GatewayReference = parsed?.PaymentId ?? paymentRequestId,
            Status = MapResultStatus(parsed?.Result),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = parsed?.NormalUrl,
            Message = parsed?.Result?.ResultMessage
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = _options.Currency.ToUpperInvariant();
        var minorUnits = ((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        var refundRequestId = $"RF_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];

        var body = new
        {
            refundRequestId,
            paymentId = request.GatewayReference,
            refundAmount = new { currency, value = minorUnits },
            refundReason = request.Reason,
            refundNotifyUrl = _options.NotifyUrl
        };

        var responseBody = await SendAsync(RefundPath, body, ct, "ProcessRefund").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AlipayRefundResponse>(responseBody);

        _logger.LogInformation("Alipay refund created: refundId={RefundId} resultCode={ResultCode}",
            parsed?.RefundId, parsed?.Result?.ResultCode);

        return new RefundResponse
        {
            GatewayReference = parsed?.RefundId ?? refundRequestId,
            Amount = request.Amount,
            Status = MapResultStatus(parsed?.Result, refundContext: true),
            ProcessedAt = DateTime.UtcNow,
            Message = parsed?.Result?.ResultMessage
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();
        var minorUnits = ((long)Math.Round(request.Amount * 100m, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
        var payoutRequestId = $"PO_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];

        var body = new
        {
            payoutRequestId,
            payoutToBeneficiary = new
            {
                beneficiaryAccountNo = request.DestinationToken,
                beneficiaryBankCode = "ALIPAY",
                beneficiaryName = request.Description
            },
            payoutAmount = new { currency, value = minorUnits }
        };

        var responseBody = await SendAsync(PayoutPath, body, ct, "ProcessPayout").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<AlipayPayoutResponse>(responseBody);

        _logger.LogInformation("Alipay payout created: payoutId={PayoutId} resultCode={ResultCode}",
            parsed?.PayoutId, parsed?.Result?.ResultCode);

        return new PayoutResponse
        {
            GatewayReference = parsed?.PayoutId ?? payoutRequestId,
            Status = MapResultStatus(parsed?.Result),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.AlipayPublicKey))
        {
            _logger.LogWarning("Alipay AlipayPublicKey not configured — webhook signature verification cannot succeed.");
            return false;
        }

        try
        {
            // Webhook signatures from Alipay+ are RSA-SHA256 over the raw JSON payload, base64-encoded.
            var sigBytes = Convert.FromBase64String(signature);
            using var rsa = LoadPublicKey(_options.AlipayPublicKey);
            return rsa.VerifyData(
                Encoding.UTF8.GetBytes(payload),
                sigBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Alipay webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhook = JsonSerializer.Deserialize<AlipayWebhookEvent>(payload);
            if (webhook is null || string.IsNullOrEmpty(webhook.NotifyType))
                return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Alipay webhook event: {NotifyType}", webhook.NotifyType);

            var status = webhook.NotifyType.ToUpperInvariant() switch
            {
                "PAYMENT_RESULT" => MapAlipayResultCode(webhook.Result?.ResultCode),
                "REFUND_RESULT" => PaymentStatus.Refunded,
                "PAYOUT_RESULT" => MapAlipayResultCode(webhook.Result?.ResultCode),
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(webhook.PaymentId))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhook.PaymentId,
                Status = status.Value,
                EventType = webhook.NotifyType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Alipay webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // === Internal helpers ===

    private async Task<string> SendAsync(string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        var requestTime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var signature = SignRequest("POST", path, _options.ClientId, requestTime, json, _options.MerchantPrivateKey);

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("client-id", _options.ClientId);
        req.Headers.Add("request-time", requestTime);
        req.Headers.Add("signature", $"algorithm=RSA256,keyVersion=1,signature={signature}");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Alipay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Alipay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// Build the Alipay+ canonical string and RSA-SHA256 sign it.
    /// Canonical = "POST &lt;path&gt;\n&lt;client-id&gt;.&lt;request-time&gt;.&lt;body&gt;".
    /// </summary>
    private static string SignRequest(string method, string path, string clientId, string requestTime, string body, string privateKeyPem)
    {
        var canonical = $"{method} {path}\n{clientId}.{requestTime}.{body}";
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
        var rsa = RSA.Create();
        var trimmed = StripPemHeaders(pem);
        var keyBytes = Convert.FromBase64String(trimmed);
        try
        {
            rsa.ImportSubjectPublicKeyInfo(keyBytes, out _);
        }
        catch (CryptographicException)
        {
            rsa.ImportRSAPublicKey(keyBytes, out _);
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

    private static PaymentStatus MapResultStatus(AlipayResult? result, bool refundContext = false)
    {
        if (result is null) return PaymentStatus.Pending;
        var code = result.ResultCode?.ToUpperInvariant() ?? string.Empty;
        if (code == "SUCCESS") return refundContext ? PaymentStatus.Refunded : PaymentStatus.Completed;
        var status = result.ResultStatus?.ToUpperInvariant() ?? string.Empty;
        return status switch
        {
            "S" => refundContext ? PaymentStatus.Refunded : PaymentStatus.Completed,
            "U" or "PROCESSING" => PaymentStatus.Pending,
            "F" or "FAIL" or "FAILED" => PaymentStatus.Failed,
            _ => PaymentStatus.Pending
        };
    }

    private static PaymentStatus MapAlipayResultCode(string? code) => code?.ToUpperInvariant() switch
    {
        "SUCCESS" or "PAYMENT_SUCCESS" => PaymentStatus.Completed,
        "PROCESSING" => PaymentStatus.Pending,
        "FAILED" or "FAIL" or "PAYMENT_FAIL" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // === Alipay+ response/webhook shapes (internal) ===

    private sealed class AlipayResult
    {
        [JsonPropertyName("resultCode")] public string? ResultCode { get; set; }
        [JsonPropertyName("resultStatus")] public string? ResultStatus { get; set; }
        [JsonPropertyName("resultMessage")] public string? ResultMessage { get; set; }
    }

    private sealed class AlipayPaymentResponse
    {
        [JsonPropertyName("result")] public AlipayResult? Result { get; set; }
        [JsonPropertyName("paymentId")] public string? PaymentId { get; set; }
        [JsonPropertyName("paymentRequestId")] public string? PaymentRequestId { get; set; }
        [JsonPropertyName("normalUrl")] public string? NormalUrl { get; set; }
    }

    private sealed class AlipayRefundResponse
    {
        [JsonPropertyName("result")] public AlipayResult? Result { get; set; }
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("refundRequestId")] public string? RefundRequestId { get; set; }
    }

    private sealed class AlipayPayoutResponse
    {
        [JsonPropertyName("result")] public AlipayResult? Result { get; set; }
        [JsonPropertyName("payoutId")] public string? PayoutId { get; set; }
        [JsonPropertyName("payoutRequestId")] public string? PayoutRequestId { get; set; }
    }

    private sealed class AlipayWebhookEvent
    {
        [JsonPropertyName("notifyType")] public string? NotifyType { get; set; }
        [JsonPropertyName("paymentId")] public string? PaymentId { get; set; }
        [JsonPropertyName("paymentRequestId")] public string? PaymentRequestId { get; set; }
        [JsonPropertyName("result")] public AlipayResult? Result { get; set; }
    }
}
