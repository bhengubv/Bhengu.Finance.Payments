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
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Bhengu.Finance.Payments.Fawry.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Fawry.Providers;

/// <summary>
/// Fawry (Egypt) payment gateway provider. Wraps the Fawry ECommerce REST API.
/// Fawry's standard merchant API does NOT expose payouts — <see cref="IPayoutProvider"/>
/// is intentionally not implemented. Merchants requiring disbursements must use
/// the Fawry Disbursement product (separate contract + API).
/// </summary>
public sealed class FawryPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private const string LiveDefaultUrl = "https://atfawry.fawrystaging.com/ECommerceWeb/api/";
    private const string SandboxDefaultUrl = "https://atfawry.fawrystaging.com/ECommerceWeb/api/";

    private readonly HttpClient _httpClient;
    private readonly FawryOptions _options;
    private readonly FawryIdempotencyCache? _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Fawry;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public FawryPaymentProvider(
        HttpClient httpClient,
        IOptions<FawryOptions> options,
        ILogger<FawryPaymentProvider> logger,
        FawryIdempotencyCache? idempotency = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency;

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

    /// <inheritdoc />
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? ProcessPaymentCoreAsync(request, ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "charge",
                () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
        => RunChargeAsync(request.Currency, async () =>
        {
            var merchantRefNum = request.Metadata?.GetValueOrDefault("merchantRefNum")
                ?? request.IdempotencyKey
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

            Logger.LogInformation("Fawry charge created: ref={Ref} status={Status} code={Code}",
                fawryResponse?.ReferenceNumber, fawryResponse?.OrderStatus, fawryResponse?.StatusCode);

            var gatewayRef = fawryResponse?.ReferenceNumber
                ?? fawryResponse?.MerchantRefNumber
                ?? merchantRefNum;
            var status = MapStatus(fawryResponse?.OrderStatus, fawryResponse?.StatusCode);

            return new PaymentResponse
            {
                GatewayReference = gatewayRef,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = fawryResponse?.StatusDescription ?? fawryResponse?.OrderStatus
            };
        }, ct);

    /// <inheritdoc />
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? ProcessRefundCoreAsync(request, ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "refund",
                () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
        => RunRefundAsync(request.GatewayReference, async () =>
        {
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

            Logger.LogInformation("Fawry refund created for ref={Ref} status={Status}",
                request.GatewayReference, refundResponse?.StatusCode);

            var status = MapRefundStatus(refundResponse?.StatusCode);

            return new RefundResponse
            {
                GatewayReference = request.GatewayReference,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = refundResponse?.StatusDescription
            };
        }, ct);

    /// <inheritdoc />
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_options.SecurityKey))
                {
                    Logger.LogWarning("Fawry SecurityKey not configured — webhook signature verification cannot succeed.");
                    return false;
                }

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
                Logger.LogError(ex, "Fawry webhook signature verification raised");
                return false;
            }
        });
    }

    /// <inheritdoc />
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () =>
        {
            try
            {
                var notification = JsonSerializer.Deserialize<FawryNotification>(payload);
                if (notification is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Fawry notification: status={Status} ref={Ref}",
                    notification.OrderStatus, notification.FawryRefNumber);

                return Task.FromResult(MapWebhookEvent(notification));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Fawry notification");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(FawryNotification n)
    {
        var rawReference = n.FawryRefNumber ?? n.MerchantRefNumber ?? n.ReferenceNumber;
        if (string.IsNullOrEmpty(rawReference)) return null;

        var amount = decimal.TryParse(n.PaymentAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var a)
            ? a
            : (decimal.TryParse(n.OrderAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var oa) ? oa : 0m);
        var currency = n.Currency ?? "EGP";

        switch (n.OrderStatus?.ToUpperInvariant())
        {
            case "PAID":
            case "DELIVERED":
                return new ChargeSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = n.OrderStatus,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = n.CustomerProfileId,
                    PaymentMethodToken = n.PaymentMethod
                };

            case "NEW":
            case "PENDING":
                return new ChargePendingEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Pending,
                    EventType = n.OrderStatus,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                };

            case "EXPIRED":
            case "FAILED":
                return new ChargeFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = n.OrderStatus,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = n.FailureErrorCode,
                    FailureMessage = n.FailureReason
                };

            case "REFUNDED":
                return new RefundSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Refunded,
                    EventType = n.OrderStatus,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = n.RefundReference ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "REFUND_FAILED":
                return new RefundFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = n.OrderStatus,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = n.FailureErrorCode,
                    FailureMessage = n.FailureReason
                };

            case "SETTLED":
            case "SETTLEMENT_COMPLETED":
                return new SettlementCompletedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = n.OrderStatus,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = rawReference,
                    NetAmount = amount,
                    Currency = currency
                };

            default:
                return null;
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
            Logger.LogError("Fawry {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
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
        [JsonPropertyName("orderAmount")] public string? OrderAmount { get; set; }
        [JsonPropertyName("paymentMethod")] public string? PaymentMethod { get; set; }
        [JsonPropertyName("currencyCode")] public string? Currency { get; set; }
        [JsonPropertyName("customerProfileId")] public string? CustomerProfileId { get; set; }
        [JsonPropertyName("failureErrorCode")] public string? FailureErrorCode { get; set; }
        [JsonPropertyName("failureReason")] public string? FailureReason { get; set; }
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
    }
}
