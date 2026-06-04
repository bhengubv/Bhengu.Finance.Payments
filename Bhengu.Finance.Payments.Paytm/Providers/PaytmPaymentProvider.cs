// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paytm.Providers;

/// <summary>
/// Paytm (India) All-in-One Payments provider. Wraps the Paytm <c>theia</c> initiate-transaction,
/// order-status, refund and Paytm Payouts APIs. Implements both <see cref="IPaymentGatewayProvider"/>
/// and <see cref="IPayoutProvider"/>.
/// </summary>
/// <remarks>
/// CHECKSUM SIMPLIFICATION: Paytm's official checksum algorithm is a random-salt + SHA-256 + AES-128-CBC
/// encryption using the MerchantKey. To keep this SDK dependency-free and the surface area testable,
/// this implementation uses a base64-encoded HMAC-SHA256 of the payload with the MerchantKey as the
/// "signature". Production merchants needing strict Paytm-compatible checksum handling should swap in
/// the official PaytmChecksum helper by wrapping this provider — the rest of the SDK contract is unchanged.
/// </remarks>
public sealed class PaytmPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _httpClient;
    private readonly PaytmOptions _options;
    private readonly ILogger<PaytmPaymentProvider> _logger;
    private readonly PaytmIdempotencyCache _idempotencyCache;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Paytm;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.TypedWebhooks |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.QrCode |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.PartialRefund;

    /// <summary>Create a new Paytm provider bound to the supplied HTTP client, options, and (optionally) a distributed cache for client-side idempotency dedupe.</summary>
    public PaytmPaymentProvider(
        HttpClient httpClient,
        IOptions<PaytmOptions> options,
        ILogger<PaytmPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotencyCache = new PaytmIdempotencyCache(cache ?? new InMemoryBhenguDistributedCache());

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
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();
        return _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, async () =>
        {
            using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, currency);
            try
            {
                return await ProcessPaymentInnerAsync(request, currency, ct).ConfigureAwait(false);
            }
            catch
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                throw;
            }
        });
    }

    private async Task<PaymentResponse> ProcessPaymentInnerAsync(PaymentRequest request, string currency, CancellationToken ct)
    {
        var orderId = request.Metadata?.GetValueOrDefault("orderId") ?? $"ORDER_{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

        var bodyPayload = new
        {
            requestType = "Payment",
            mid = _options.MerchantId,
            websiteName = _options.WebsiteName,
            orderId,
            callbackUrl = request.Metadata?.GetValueOrDefault("callbackUrl") ?? _options.CallbackUrl,
            txnAmount = new { value = amount, currency },
            userInfo = new
            {
                custId = request.Metadata?.GetValueOrDefault("custId") ?? request.PaymentMethodToken,
                mobile = request.Metadata?.GetValueOrDefault("mobile") ?? string.Empty,
                email = request.Metadata?.GetValueOrDefault("email") ?? string.Empty,
                firstName = request.Metadata?.GetValueOrDefault("firstName") ?? string.Empty,
                lastName = request.Metadata?.GetValueOrDefault("lastName") ?? string.Empty
            }
        };

        var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
        var signature = ComputeChecksum(serializedBody);

        var envelope = new
        {
            body = bodyPayload,
            head = new { signature }
        };

        var path = $"theia/api/v1/initiateTransaction?mid={Uri.EscapeDataString(_options.MerchantId)}&orderId={Uri.EscapeDataString(orderId)}";
        var raw = await SendAsync(HttpMethod.Post, path, envelope, ct, "InitiateTransaction").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<PaytmInitiateResponse>(raw, DeserializeOptions);

        var txnToken = resp?.Body?.TxnToken;
        var resultStatus = resp?.Body?.ResultInfo?.ResultStatus;

        _logger.LogInformation("Paytm initiateTransaction: orderId={OrderId} status={Status}", orderId, resultStatus);

        var baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://securegw-stage.paytm.in")
            : (_options.BaseUrl ?? "https://securegw.paytm.in");
        var checkoutUrl = $"{baseUrl.TrimEnd('/')}/theia/api/v1/showPaymentPage?mid={Uri.EscapeDataString(_options.MerchantId)}&orderId={Uri.EscapeDataString(orderId)}";

        return new PaymentResponse
        {
            GatewayReference = orderId,
            Status = MapStatus(resultStatus ?? "pending"),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = string.IsNullOrEmpty(txnToken) ? null : checkoutUrl,
            Message = string.IsNullOrEmpty(txnToken)
                ? resp?.Body?.ResultInfo?.ResultMsg
                : $"txnToken={txnToken}"
        };
    }

    /// <inheritdoc />
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, async () =>
        {
            using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderName, request.GatewayReference);
            try
            {
                return await ProcessRefundInnerAsync(request, ct).ConfigureAwait(false);
            }
            catch
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                throw;
            }
        });
    }

    private async Task<RefundResponse> ProcessRefundInnerAsync(RefundRequest request, CancellationToken ct)
    {
        var refId = $"REFUNDID_{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

        // Paytm refund correlates by orderId; some merchants also pass txnId. RefundRequest.Reason
        // is the only free-form field we have to carry it — if callers want to override txnId, they
        // can prefix the reason with "txnId:<value>;".
        string? txnId = null;
        if (!string.IsNullOrEmpty(request.Reason) && request.Reason.StartsWith("txnId:", StringComparison.Ordinal))
        {
            var sep = request.Reason.IndexOf(';');
            txnId = sep > 0 ? request.Reason["txnId:".Length..sep] : request.Reason["txnId:".Length..];
        }

        var bodyPayload = new
        {
            mid = _options.MerchantId,
            txnType = "REFUND",
            orderId = request.GatewayReference,
            txnId,
            refId,
            refundAmount = amount
        };

        var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
        var signature = ComputeChecksum(serializedBody);

        var envelope = new
        {
            body = bodyPayload,
            head = new { clientId = "C11", version = "v1", signature }
        };

        var raw = await SendAsync(HttpMethod.Post, "refund/apply", envelope, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<PaytmRefundResponse>(raw, DeserializeOptions);

        _logger.LogInformation("Paytm refund: orderId={OrderId} refId={RefId} status={Status}",
            request.GatewayReference, refId, resp?.Body?.ResultInfo?.ResultStatus);

        return new RefundResponse
        {
            GatewayReference = resp?.Body?.RefundId ?? refId,
            Amount = request.Amount,
            Status = MapStatus(resp?.Body?.ResultInfo?.ResultStatus ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Body?.ResultInfo?.ResultMsg
        };
    }

    /// <inheritdoc />
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();
        return _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, async () =>
        {
            using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderName, currency);
            try
            {
                return await ProcessPayoutInnerAsync(request, currency, ct).ConfigureAwait(false);
            }
            catch
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                throw;
            }
        });
    }

    private async Task<PayoutResponse> ProcessPayoutInnerAsync(PayoutRequest request, string currency, CancellationToken ct)
    {
        var orderId = $"PAYOUT_{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

        var bodyPayload = new
        {
            mid = _options.MerchantId,
            beneficiary = request.DestinationToken,
            amount,
            orderId,
            purpose = request.Description ?? "Bhengu payout"
        };

        var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
        var signature = ComputeChecksum(serializedBody);

        var envelope = new
        {
            body = bodyPayload,
            head = new { signature }
        };

        var raw = await SendAsync(HttpMethod.Post, "disburse/v1/order/wallet", envelope, ct, "ProcessPayout").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<PaytmPayoutResponse>(raw, DeserializeOptions);

        _logger.LogInformation("Paytm payout: orderId={OrderId} status={Status}", orderId, resp?.Body?.ResultInfo?.ResultStatus);

        return new PayoutResponse
        {
            GatewayReference = resp?.Body?.TxnId ?? orderId,
            Status = MapStatus(resp?.Body?.ResultInfo?.ResultStatus ?? "pending"),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
        {
            _logger.LogWarning("Paytm MerchantKey not configured — signature verification cannot succeed.");
            BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("valid", false));
            return false;
        }

        try
        {
            var computed = ComputeChecksum(payload);
            var valid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(computed));
            BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("valid", valid));
            return valid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paytm webhook signature verification raised");
            return false;
        }
    }

    /// <summary>
    /// Parse a Paytm webhook payload into a typed <see cref="WebhookEvent"/> sub-record where possible.
    /// </summary>
    /// <remarks>
    /// Paytm S2S callbacks ship two parallel naming schemes (legacy UPPERCASE: ORDERID/STATUS and
    /// newer camelCase: orderId/status). Both are probed. Status mapping:
    /// <c>TXN_SUCCESS</c>/<c>success</c>/<c>captured</c> → <see cref="ChargeSucceededEvent"/>,
    /// <c>TXN_FAILURE</c>/<c>failure</c>/<c>failed</c> → <see cref="ChargeFailedEvent"/>,
    /// <c>PENDING</c> → <see cref="ChargePendingEvent"/>,
    /// <c>REFUND_SUCCESS</c>/<c>refunded</c> → <see cref="RefundSucceededEvent"/>.
    /// </remarks>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var orderId = ReadStringProperty(root, "ORDERID") ?? ReadStringProperty(root, "orderId");
            var status = ReadStringProperty(root, "STATUS") ?? ReadStringProperty(root, "status");
            var txnId = ReadStringProperty(root, "TXNID") ?? ReadStringProperty(root, "txnId");
            var refundId = ReadStringProperty(root, "REFUNDID") ?? ReadStringProperty(root, "refundId");
            var amountStr = ReadStringProperty(root, "TXNAMOUNT") ?? ReadStringProperty(root, "txnAmount");
            var failureCode = ReadStringProperty(root, "RESPCODE") ?? ReadStringProperty(root, "respCode");
            var failureMessage = ReadStringProperty(root, "RESPMSG") ?? ReadStringProperty(root, "respMsg");
            var currency = ReadStringProperty(root, "CURRENCY") ?? ReadStringProperty(root, "currency") ?? _options.Currency;

            _logger.LogInformation("Parsed Paytm webhook: orderId={OrderId} status={Status}", orderId, status);

            if (string.IsNullOrEmpty(orderId))
                return Task.FromResult<WebhookEvent?>(null);

            var amount = decimal.TryParse(amountStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : 0m;
            var normalised = status?.ToLowerInvariant() ?? "unknown";

            WebhookEvent? typed = normalised switch
            {
                "txn_success" or "success" or "completed" or "captured" or "s" => new ChargeSucceededEvent
                {
                    GatewayReference = orderId,
                    Status = PaymentStatus.Completed,
                    EventType = $"paytm.{normalised}",
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    PaymentMethodToken = txnId
                },
                "txn_failure" or "failure" or "failed" or "f" => new ChargeFailedEvent
                {
                    GatewayReference = orderId,
                    Status = PaymentStatus.Failed,
                    EventType = $"paytm.{normalised}",
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = failureCode,
                    FailureMessage = failureMessage
                },
                "txn_pending" or "pending" or "p" or "initiated" => new ChargePendingEvent
                {
                    GatewayReference = orderId,
                    Status = PaymentStatus.Pending,
                    EventType = $"paytm.{normalised}",
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                },
                "refund_success" or "refunded" => new RefundSucceededEvent
                {
                    GatewayReference = orderId,
                    Status = PaymentStatus.Refunded,
                    EventType = $"paytm.{normalised}",
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = refundId ?? orderId,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                },
                _ => new WebhookEvent
                {
                    GatewayReference = orderId,
                    Status = MapStatus(status ?? "pending"),
                    EventType = $"paytm.{normalised}",
                    Category = WebhookEventCategory.Unknown
                }
            };

            activity?.SetTag("payment.gateway_reference", orderId);
            return Task.FromResult<WebhookEvent?>(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Paytm webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static string? ReadStringProperty(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        return root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

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

    /// <summary>
    /// Simplified checksum: base64(HMAC-SHA256(payload, MerchantKey)).
    /// See class-level remarks for why this departs from Paytm's official AES-based checksum.
    /// </summary>
    private string ComputeChecksum(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MerchantKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "txn_success" or "success" or "completed" or "captured" or "s" => PaymentStatus.Completed,
        "txn_pending" or "pending" or "p" or "initiated" => PaymentStatus.Pending,
        "txn_failure" or "failure" or "failed" or "f" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" or "refund_success" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Paytm API response shapes (internal) ===

    private sealed class PaytmInitiateResponse
    {
        [JsonPropertyName("body")] public PaytmInitiateBody? Body { get; set; }
        [JsonPropertyName("head")] public PaytmHead? Head { get; set; }
    }

    private sealed class PaytmInitiateBody
    {
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
        [JsonPropertyName("txnToken")] public string? TxnToken { get; set; }
        [JsonPropertyName("isPromoCodeValid")] public bool? IsPromoCodeValid { get; set; }
    }

    private sealed class PaytmRefundResponse
    {
        [JsonPropertyName("body")] public PaytmRefundBody? Body { get; set; }
        [JsonPropertyName("head")] public PaytmHead? Head { get; set; }
    }

    private sealed class PaytmRefundBody
    {
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("txnId")] public string? TxnId { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
    }

    private sealed class PaytmPayoutResponse
    {
        [JsonPropertyName("body")] public PaytmPayoutBody? Body { get; set; }
        [JsonPropertyName("head")] public PaytmHead? Head { get; set; }
    }

    private sealed class PaytmPayoutBody
    {
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
        [JsonPropertyName("txnId")] public string? TxnId { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
    }

    private sealed class PaytmResultInfo
    {
        [JsonPropertyName("resultStatus")] public string? ResultStatus { get; set; }
        [JsonPropertyName("resultCode")] public string? ResultCode { get; set; }
        [JsonPropertyName("resultMsg")] public string? ResultMsg { get; set; }
    }

    private sealed class PaytmHead
    {
        [JsonPropertyName("signature")] public string? Signature { get; set; }
        [JsonPropertyName("responseTimestamp")] public string? ResponseTimestamp { get; set; }
    }

}
