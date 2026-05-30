// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Fawry.Providers;

/// <summary>
/// Fawry (Egypt) payment gateway provider. Wraps the Fawry ECommerce REST API.
/// Fawry's standard merchant API does NOT expose payouts — <see cref="IPayoutProvider"/>
/// is intentionally not implemented. Merchants requiring disbursements must use
/// the Fawry Disbursement product (separate contract + API).
/// </summary>
public sealed class FawryPaymentProvider : IPaymentGatewayProvider
{
    private const string LiveDefaultUrl = "https://atfawry.fawrystaging.com/ECommerceWeb/api/";
    private const string SandboxDefaultUrl = "https://atfawry.fawrystaging.com/ECommerceWeb/api/";

    private readonly HttpClient _httpClient;
    private readonly FawryOptions _options;
    private readonly ILogger<FawryPaymentProvider> _logger;

    public string ProviderName => "fawry";

    public FawryPaymentProvider(
        HttpClient httpClient,
        IOptions<FawryOptions> options,
        ILogger<FawryPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FawryOptions.MerchantCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecurityKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FawryOptions.SecurityKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var url = _options.UseSandbox
                ? _options.SandboxUrl ?? SandboxDefaultUrl
                : _options.BaseUrl ?? LiveDefaultUrl;
            _httpClient.BaseAddress = new Uri(url);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var merchantRefNum = request.Metadata?.GetValueOrDefault("merchantRefNum")
            ?? $"fawry-{Guid.NewGuid():N}";
        var customerProfileId = request.Metadata?.GetValueOrDefault("customerProfileId") ?? string.Empty;
        var customerName = request.Metadata?.GetValueOrDefault("customerName") ?? string.Empty;
        var customerMobile = request.Metadata?.GetValueOrDefault("customerMobile") ?? string.Empty;
        var customerEmail = request.Metadata?.GetValueOrDefault("customerEmail") ?? string.Empty;
        var paymentMethod = request.Metadata?.GetValueOrDefault("paymentMethod") ?? _options.DefaultPaymentMethod;
        var amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);

        var signature = ComputeChargeSignature(
            _options.MerchantCode, merchantRefNum, customerProfileId, paymentMethod, amount, _options.SecurityKey);

        var requestBody = new
        {
            merchantCode = _options.MerchantCode,
            merchantRefNum,
            customerName,
            customerMobile,
            customerEmail,
            customerProfileId,
            paymentMethod,
            amount,
            currencyCode = request.Currency.ToUpperInvariant(),
            description = request.Description,
            returnUrl = _options.ReturnUrl,
            chargeItems = Array.Empty<object>(),
            signature
        };

        var body = await SendAsync(HttpMethod.Post, "payments/charge", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var fawryResponse = JsonSerializer.Deserialize<FawryChargeResponse>(body);

        _logger.LogInformation("Fawry charge created: ref={Ref} status={Status} code={Code}",
            fawryResponse?.ReferenceNumber, fawryResponse?.OrderStatus, fawryResponse?.StatusCode);

        // Fawry returns "12000" in statusCode for accepted requests; the actual payment state
        // lives in orderStatus and arrives asynchronously by webhook for PAYATFAWRY/MWALLET flows.
        var gatewayRef = fawryResponse?.ReferenceNumber
            ?? fawryResponse?.MerchantRefNumber
            ?? merchantRefNum;

        return new PaymentResponse
        {
            GatewayReference = gatewayRef,
            Status = MapStatus(fawryResponse?.OrderStatus, fawryResponse?.StatusCode),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = fawryResponse?.StatusDescription ?? fawryResponse?.OrderStatus
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var refundAmount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var signature = ComputeRefundSignature(
            _options.MerchantCode, request.GatewayReference, refundAmount, request.Reason, _options.SecurityKey);

        var requestBody = new
        {
            merchantCode = _options.MerchantCode,
            referenceNumber = request.GatewayReference,
            refundAmount,
            reason = request.Reason,
            signature
        };

        var body = await SendAsync(HttpMethod.Post, "payments/refund", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<FawryRefundResponse>(body);

        _logger.LogInformation("Fawry refund created for ref={Ref} status={Status}",
            request.GatewayReference, refundResponse?.StatusCode);

        return new RefundResponse
        {
            GatewayReference = request.GatewayReference,
            Amount = request.Amount,
            Status = MapRefundStatus(refundResponse?.StatusCode),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.StatusDescription
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.SecurityKey))
        {
            _logger.LogWarning("Fawry SecurityKey not configured — webhook signature verification cannot succeed.");
            return false;
        }

        try
        {
            // Fawry concatenates webhook fields in a documented order then SHA-256 hex-lowercase.
            // The caller is responsible for supplying the already-concatenated payload that matches
            // Fawry's notification spec: fawryRefNumber + merchantRefNum + paymentAmount + orderAmount +
            //   orderStatus + paymentMethod + paymentReferenceNumber + securityKey.
            // We hash that string and compare to the supplied signature.
            var canonical = payload + _options.SecurityKey;
            var computed = Sha256Hex(canonical);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fawry webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var notification = JsonSerializer.Deserialize<FawryNotification>(payload);
            if (notification is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Fawry notification: status={Status} ref={Ref}",
                notification.OrderStatus, notification.FawryRefNumber);

            var status = notification.OrderStatus?.ToUpperInvariant() switch
            {
                "NEW" or "PENDING" => PaymentStatus.Pending,
                "PAID" => PaymentStatus.Completed,
                "DELIVERED" => PaymentStatus.Completed,
                "REFUNDED" => PaymentStatus.Refunded,
                "EXPIRED" => PaymentStatus.Failed,
                "CANCELED" or "CANCELLED" => PaymentStatus.Cancelled,
                "FAILED" => PaymentStatus.Failed,
                _ => (PaymentStatus?)null
            };

            if (status is null)
                return Task.FromResult<WebhookEvent?>(null);

            var gatewayRef = notification.FawryRefNumber
                ?? notification.MerchantRefNumber
                ?? notification.ReferenceNumber;
            if (string.IsNullOrEmpty(gatewayRef))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = gatewayRef,
                Status = status.Value,
                EventType = notification.OrderStatus
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Fawry notification");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Fawry failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Fawry {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    internal static string ComputeChargeSignature(
        string merchantCode, string merchantRefNum, string customerProfileId,
        string paymentMethod, string amount, string securityKey)
    {
        var canonical = merchantCode + merchantRefNum + customerProfileId + paymentMethod + amount + securityKey;
        return Sha256Hex(canonical);
    }

    internal static string ComputeRefundSignature(
        string merchantCode, string referenceNumber, string refundAmount, string reason, string securityKey)
    {
        var canonical = merchantCode + referenceNumber + refundAmount + (reason ?? string.Empty) + securityKey;
        return Sha256Hex(canonical);
    }

    private static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static PaymentStatus MapStatus(string? orderStatus, string? statusCode)
    {
        var s = (orderStatus ?? string.Empty).ToUpperInvariant();
        return s switch
        {
            "PAID" or "DELIVERED" => PaymentStatus.Completed,
            "NEW" or "PENDING" or "" when statusCode is "12000" or "200" => PaymentStatus.Pending,
            "NEW" or "PENDING" => PaymentStatus.Pending,
            "REFUNDED" => PaymentStatus.Refunded,
            "EXPIRED" or "FAILED" => PaymentStatus.Failed,
            "CANCELED" or "CANCELLED" => PaymentStatus.Cancelled,
            _ => PaymentStatus.Pending
        };
    }

    private static PaymentStatus MapRefundStatus(string? statusCode) => statusCode switch
    {
        "200" or "12000" => PaymentStatus.Refunded,
        null or "" => PaymentStatus.Pending,
        _ => PaymentStatus.Failed
    };

    // === Fawry API response shapes (internal) ===

    private sealed class FawryChargeResponse
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("referenceNumber")] public string? ReferenceNumber { get; set; }
        [JsonPropertyName("merchantRefNumber")] public string? MerchantRefNumber { get; set; }
        [JsonPropertyName("orderStatus")] public string? OrderStatus { get; set; }
        [JsonPropertyName("statusCode")] public string? StatusCode { get; set; }
        [JsonPropertyName("statusDescription")] public string? StatusDescription { get; set; }
    }

    private sealed class FawryRefundResponse
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("statusCode")] public string? StatusCode { get; set; }
        [JsonPropertyName("statusDescription")] public string? StatusDescription { get; set; }
    }

    private sealed class FawryNotification
    {
        [JsonPropertyName("fawryRefNumber")] public string? FawryRefNumber { get; set; }
        [JsonPropertyName("merchantRefNumber")] public string? MerchantRefNumber { get; set; }
        [JsonPropertyName("referenceNumber")] public string? ReferenceNumber { get; set; }
        [JsonPropertyName("orderStatus")] public string? OrderStatus { get; set; }
        [JsonPropertyName("paymentAmount")] public string? PaymentAmount { get; set; }
        [JsonPropertyName("paymentMethod")] public string? PaymentMethod { get; set; }
    }
}
