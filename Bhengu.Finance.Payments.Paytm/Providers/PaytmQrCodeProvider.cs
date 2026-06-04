// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paytm.Providers;

/// <summary>
/// Paytm Smart QR provider. Wraps the Paytm <c>POST /paymentservices/qr/create</c> endpoint
/// for both dynamic (amount-locked) and static (payer-enters-amount) QR codes.
/// </summary>
/// <remarks>
/// Paytm Smart QR encodes a Paytm-specific UPI string (e.g.
/// <c>upi://pay?pa=paytmqr@paytm&amp;pn=…&amp;am=…&amp;tr=…&amp;cu=INR</c>) whose <c>signature</c>
/// is HMAC-SHA256(body, MerchantKey) per Paytm's S2S checksum scheme. Status is polled via
/// <c>POST /paymentservices/qr/getStatus</c>.
/// </remarks>
public sealed class PaytmQrCodeProvider : IQrCodeProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _httpClient;
    private readonly PaytmOptions _options;
    private readonly ILogger<PaytmQrCodeProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Paytm;

    /// <summary>Create a new Paytm Smart QR provider.</summary>
    public PaytmQrCodeProvider(
        HttpClient httpClient,
        IOptions<PaytmOptions> options,
        ILogger<PaytmQrCodeProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://securegw-stage.paytm.in/")
                : (_options.BaseUrl ?? "https://securegw.paytm.in/"));
        }
    }

    /// <inheritdoc />
    public async Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "qr.generate");
        try
        {
            var posId = $"POS_{Guid.NewGuid():N}"[..16];
            var bodyPayload = new
            {
                mid = _options.MerchantId,
                orderId = request.MerchantReference,
                businessType = "UPI_QR_CODE",
                posId,
                displayValue = request.Amount?.ToString("F2", CultureInfo.InvariantCulture)
            };

            var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
            var signature = ComputeChecksum(serializedBody);
            var envelope = new { body = bodyPayload, head = new { clientId = "C11", version = "v1", signature } };

            var raw = await SendAsync(HttpMethod.Post, "paymentservices/qr/create", envelope, ct, "GenerateQr").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaytmQrEnvelope<PaytmQrCreateBody>>(raw, DeserializeOptions);

            _logger.LogInformation("Paytm QR created: orderId={OrderId} qrId={QrId} status={Status}",
                request.MerchantReference, response?.Body?.QrCodeId, response?.Body?.ResultInfo?.ResultStatus);

            if (response?.Body?.ResultInfo?.ResultStatus is "F" or "FAILURE")
                throw new BhenguPaymentException(ProviderName, response.Body.ResultInfo.ResultMsg ?? "Paytm QR creation failed",
                    providerErrorCode: response.Body.ResultInfo.ResultCode);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return new QrCode
            {
                Reference = response?.Body?.QrCodeId ?? request.MerchantReference,
                Format = QrFormat.Payload,
                Payload = response?.Body?.QrData ?? response?.Body?.Image,
                Amount = request.Amount,
                Currency = request.Currency.ToUpperInvariant(),
                ExpiresAt = request.ExpiresAt
            };
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
            var bodyPayload = new { mid = _options.MerchantId, qrCodeId = qrReference };
            var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
            var signature = ComputeChecksum(serializedBody);
            var envelope = new { body = bodyPayload, head = new { signature } };

            var raw = await SendAsync(HttpMethod.Post, "paymentservices/qr/getStatus", envelope, ct, "GetQrStatus").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaytmQrEnvelope<PaytmQrStatusBody>>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            // Prefer the actual transaction status (PENDING/TXN_SUCCESS/TXN_FAILURE) over the
            // API resultStatus (S/F — which only tells us the API call succeeded).
            return MapStatus(response?.Body?.TxnStatus ?? response?.Body?.ResultInfo?.ResultStatus);
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "txn_success" or "success" or "captured" or "completed" or "s" => PaymentStatus.Completed,
        "pending" or "p" or "initiated" => PaymentStatus.Pending,
        "txn_failure" or "failure" or "failed" or "f" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, SerializeOptions);
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Paytm failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Paytm {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private string ComputeChecksum(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MerchantKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private sealed class PaytmQrEnvelope<TBody>
    {
        [JsonPropertyName("body")] public TBody? Body { get; set; }
    }

    private sealed class PaytmQrCreateBody
    {
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
        [JsonPropertyName("qrCodeId")] public string? QrCodeId { get; set; }
        [JsonPropertyName("qrData")] public string? QrData { get; set; }
        [JsonPropertyName("image")] public string? Image { get; set; }
    }

    private sealed class PaytmQrStatusBody
    {
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
        [JsonPropertyName("txnStatus")] public string? TxnStatus { get; set; }
    }

    private sealed class PaytmResultInfo
    {
        [JsonPropertyName("resultStatus")] public string? ResultStatus { get; set; }
        [JsonPropertyName("resultCode")] public string? ResultCode { get; set; }
        [JsonPropertyName("resultMsg")] public string? ResultMsg { get; set; }
    }
}
