// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Slydepay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Slydepay.Providers;

/// <summary>
/// Slydepay (Ghana) mobile-first wallet provider. Wraps the legacy paymentservice.asmx
/// JSON API: ProcessPaymentOrder, VerifyTransactionStatus, CancelTransactionStatus.
/// Slydepay has no native refund or payout API; both throw.
/// Webhook authenticity is verified by re-calling VerifyTransactionStatus (no HMAC is issued).
/// </summary>
public sealed class SlydepayPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly SlydepayOptions _options;
    private readonly ILogger<SlydepayPaymentProvider> _logger;

    public string ProviderName => "slydepay";

    public SlydepayPaymentProvider(
        HttpClient httpClient,
        IOptions<SlydepayOptions> options,
        ILogger<SlydepayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.EmailOrMobile))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(SlydepayOptions.EmailOrMobile)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(SlydepayOptions.MerchantKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://uat.slydepay.com.gh/"
                : _options.BaseUrl ?? "https://app.slydepay.com.gh/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new
        {
            emailOrMobileNumber = _options.EmailOrMobile,
            merchantKey = _options.MerchantKey,
            orderCode = request.PaymentMethodToken,
            description = request.Description,
            amount = request.Amount,
            comment1 = request.Metadata?.GetValueOrDefault("comment1") ?? "",
            comment2 = request.Metadata?.GetValueOrDefault("comment2") ?? "",
            surcharge = request.Metadata?.GetValueOrDefault("surcharge") ?? "0",
            currency = request.Currency.ToUpperInvariant(),
            paymentChannels = _options.PaymentChannels
        };

        var responseBody = await SendAsync(HttpMethod.Post, "webservices/paymentservice.asmx/ProcessPaymentOrder",
            body, ct, "ProcessPayment").ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<SlydepayEnvelope<SlydepayProcessResult>>(responseBody);
        var result = envelope?.Result;

        _logger.LogInformation("Slydepay ProcessPaymentOrder: success={Success} payToken={PayToken}",
            result?.Success, result?.PayToken);

        return new PaymentResponse
        {
            GatewayReference = result?.PayToken ?? request.PaymentMethodToken,
            Status = result?.Success == true ? PaymentStatus.Pending : PaymentStatus.Failed,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = result?.CheckOutUrl ?? envelope?.ErrorMessage
        };
    }

    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "Slydepay does not natively support refunds via the public API — issue refunds via the Slydepay merchant portal.",
            providerErrorCode: "not_supported");
    }

    /// <summary>
    /// Verify a Slydepay transaction's status via VerifyTransactionStatus. Returns the raw
    /// envelope body — callers can deserialise/inspect for fine-grained status fields.
    /// </summary>
    public async Task<string> VerifyTransactionAsync(string payToken, string orderCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payToken);
        ArgumentException.ThrowIfNullOrEmpty(orderCode);

        var body = new
        {
            emailOrMobileNumber = _options.EmailOrMobile,
            merchantKey = _options.MerchantKey,
            payToken,
            orderCode
        };
        return await SendAsync(HttpMethod.Post, "webservices/paymentservice.asmx/VerifyTransactionStatus",
            body, ct, "VerifyTransaction").ConfigureAwait(false);
    }

    /// <summary>
    /// Cancel a pending Slydepay transaction via CancelTransactionStatus.
    /// </summary>
    public async Task<string> CancelTransactionAsync(string payToken, string orderCode, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payToken);
        ArgumentException.ThrowIfNullOrEmpty(orderCode);

        var body = new
        {
            emailOrMobileNumber = _options.EmailOrMobile,
            merchantKey = _options.MerchantKey,
            payToken,
            orderCode
        };
        return await SendAsync(HttpMethod.Post, "webservices/paymentservice.asmx/CancelTransactionStatus",
            body, ct, "CancelTransaction").ConfigureAwait(false);
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
        {
            _logger.LogWarning("Slydepay MerchantKey not configured — webhook verification cannot succeed.");
            return false;
        }

        // Slydepay does NOT HMAC its PaymentNotificationUrl callbacks. Constant-time compare the
        // supplied signature with the configured MerchantKey, and additionally require callers to
        // re-confirm via VerifyTransactionAsync(payToken, orderCode) in production.
        var a = Encoding.UTF8.GetBytes(signature);
        var b = Encoding.UTF8.GetBytes(_options.MerchantKey);
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var notif = JsonSerializer.Deserialize<SlydepayNotification>(payload);
            if (notif is null || string.IsNullOrEmpty(notif.PayToken))
                return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Slydepay notification: payToken={PayToken} status={Status}",
                notif.PayToken, notif.TransactionStatus);

            var status = notif.TransactionStatus?.ToUpperInvariant() switch
            {
                "CONFIRMED" or "PAID" or "COMPLETED" or "SUCCESS" => PaymentStatus.Completed,
                "PENDING" or "PROCESSING" or "ACCEPTED" => PaymentStatus.Pending,
                "FAILED" or "DECLINED" => PaymentStatus.Failed,
                "CANCELED" or "CANCELLED" => PaymentStatus.Cancelled,
                "REFUNDED" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null) return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = notif.PayToken,
                Status = status.Value,
                EventType = notif.TransactionStatus
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Slydepay notification");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Slydepay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Slydepay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class SlydepayEnvelope<T>
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; set; }
        [JsonPropertyName("result")] public T? Result { get; set; }
    }

    private sealed class SlydepayProcessResult
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("payToken")] public string? PayToken { get; set; }
        [JsonPropertyName("qrCode")] public string? QrCode { get; set; }
        [JsonPropertyName("checkOutUrl")] public string? CheckOutUrl { get; set; }
    }

    private sealed class SlydepayNotification
    {
        [JsonPropertyName("payToken")] public string? PayToken { get; set; }
        [JsonPropertyName("orderCode")] public string? OrderCode { get; set; }
        [JsonPropertyName("transactionStatus")] public string? TransactionStatus { get; set; }
    }
}
