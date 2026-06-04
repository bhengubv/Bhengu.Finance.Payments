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
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Remita.Providers;

/// <summary>
/// Remita (SystemSpecs) payment + payout provider. Wraps the Remita REST surface for
/// Nigerian government revenue collection, corporate disbursement, e-collection, and
/// Single Send Money payouts. Authentication uses SHA-512 hex hashes of concatenated
/// fields with the configured API key — Remita does NOT use bearer tokens for these endpoints.
/// </summary>
public sealed class RemitaPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private const string PaymentInitPath =
        "remita/exapp/api/v1/send/api/echannelsvc/merchant/api/paymentinit";

    private const string SendMoneyPath =
        "remita/exapp/api/v1/send/api/echannelsvc/merchant/api/sendmoney";

    private const string RefundPath = "remita/refundservice/refund/initiate";

    private readonly HttpClient _httpClient;
    private readonly RemitaOptions _options;
    private readonly RemitaIdempotencyCache? _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Remita;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Mandates |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public RemitaPaymentProvider(
        HttpClient httpClient,
        IOptions<RemitaOptions> options,
        ILogger<RemitaPaymentProvider> logger,
        RemitaIdempotencyCache? idempotency = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency;

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ServiceTypeId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.ServiceTypeId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://remitademo.net"
                : _options.BaseUrl ?? "https://login.remita.net";
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
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
            var orderId = request.PaymentMethodToken;
            var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

            // Remita payment-init hash = SHA512(merchantId + serviceTypeId + orderId + total + apiKey).
            var hash = Sha512Hex(
                _options.MerchantId + _options.ServiceTypeId + orderId + amount + _options.ApiKey);

            var requestBody = new
            {
                serviceTypeId = _options.ServiceTypeId,
                amount,
                orderId,
                payerName = request.Metadata?.GetValueOrDefault("payerName") ?? "Bhengu Customer",
                payerEmail = request.Metadata?.GetValueOrDefault("payerEmail") ?? "noreply@bhengu.local",
                payerPhone = request.Metadata?.GetValueOrDefault("payerPhone") ?? string.Empty,
                description = request.Description,
                responseurl = _options.CallbackUrl
            };

            var authHeader = $"remitaConsumerKey={_options.MerchantId},remitaConsumerToken={hash}";
            var body = await SendAsync(HttpMethod.Post, PaymentInitPath, requestBody, ct, "ProcessPayment", authHeader)
                .ConfigureAwait(false);

            var remitaResponse = JsonSerializer.Deserialize<RemitaPaymentInitResponse>(body);

            Logger.LogInformation("Remita payment init: orderId={OrderId} rrr={Rrr} statusCode={StatusCode}",
                orderId, remitaResponse?.Rrr, remitaResponse?.StatusCode);

            var status = MapStatusCode(remitaResponse?.StatusCode);

            return new PaymentResponse
            {
                GatewayReference = remitaResponse?.Rrr ?? string.Empty,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = remitaResponse?.Status
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
            var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
            var hash = Sha512Hex(_options.MerchantId + request.GatewayReference + amount + _options.ApiKey);

            var requestBody = new
            {
                merchantId = _options.MerchantId,
                rrr = request.GatewayReference,
                amount,
                reason = request.Reason,
                hash
            };

            var authHeader = $"remitaConsumerKey={_options.MerchantId},remitaConsumerToken={hash}";
            var body = await SendAsync(HttpMethod.Post, RefundPath, requestBody, ct, "ProcessRefund", authHeader)
                .ConfigureAwait(false);

            var refundResponse = JsonSerializer.Deserialize<RemitaRefundResponse>(body);

            Logger.LogInformation("Remita refund initiated: refundRef={RefundRef} rrr={Rrr}",
                refundResponse?.RefundReference, request.GatewayReference);

            var status = MapStatusCode(refundResponse?.StatusCode);

            return new RefundResponse
            {
                GatewayReference = refundResponse?.RefundReference ?? request.GatewayReference,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = refundResponse?.Status
            };
        }, ct);

    /// <inheritdoc />
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? ProcessPayoutCoreAsync(request, ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "payout",
                () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
        => RunPayoutAsync(request.Currency, async () =>
        {
            if (string.IsNullOrWhiteSpace(_options.FromBank) || string.IsNullOrWhiteSpace(_options.DebitAccount))
                throw new ProviderConfigurationException(ProviderName,
                    "Remita payouts require FromBank and DebitAccount to be configured.");

            // DestinationToken format: "<creditBank>:<creditAccount>" (e.g. "058:0123456789").
            var colon = request.DestinationToken.IndexOf(':');
            if (colon <= 0)
                throw new BhenguPaymentException(ProviderName,
                    "Remita PayoutRequest.DestinationToken must be 'creditBankCode:creditAccountNumber'.",
                    providerErrorCode: "invalid_destination");

            var creditBank = request.DestinationToken[..colon];
            var creditAccount = request.DestinationToken[(colon + 1)..];
            var transRef = request.IdempotencyKey ?? $"sm-{Guid.NewGuid():N}";
            var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

            var hash = Sha512Hex(
                _options.MerchantId + creditAccount + amount + _options.ApiKey);

            var requestBody = new
            {
                fromBank = _options.FromBank,
                debitAccount = _options.DebitAccount,
                creditAccount,
                creditBank,
                narration = request.Description,
                amount,
                transRef,
                custName = request.Description
            };

            var authHeader = $"remitaConsumerKey={_options.MerchantId},remitaConsumerToken={hash}";
            var body = await SendAsync(HttpMethod.Post, SendMoneyPath, requestBody, ct, "ProcessPayout", authHeader)
                .ConfigureAwait(false);

            var payoutResponse = JsonSerializer.Deserialize<RemitaSendMoneyResponse>(body);

            Logger.LogInformation("Remita Single Send Money queued: transRef={TransRef} statusCode={StatusCode}",
                transRef, payoutResponse?.StatusCode);

            var status = MapStatusCode(payoutResponse?.StatusCode);

            return new PayoutResponse
            {
                GatewayReference = payoutResponse?.TransRef ?? transRef,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow
            };
        }, ct);

    /// <summary>
    /// Verify a Remita webhook callback. Remita signs callbacks with SHA-512 of
    /// (rrr + status + apiKey). <paramref name="payload"/> is interpreted as the
    /// JSON callback body and parsed to extract rrr + status; <paramref name="signature"/> is
    /// the SHA-512 hex value Remita supplies.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    Logger.LogWarning("Remita ApiKey not configured — signature verification cannot succeed.");
                    return false;
                }

                var callback = JsonSerializer.Deserialize<RemitaWebhookEvent>(payload, s_caseInsensitive);
                if (callback is null || string.IsNullOrEmpty(callback.Rrr) || string.IsNullOrEmpty(callback.Status))
                    return false;

                var expected = Sha512Hex(callback.Rrr + callback.Status + _options.ApiKey);
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature),
                    Encoding.UTF8.GetBytes(expected));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Remita webhook signature verification raised");
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
                var callback = JsonSerializer.Deserialize<RemitaWebhookEvent>(payload, s_caseInsensitive);
                if (callback is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Remita webhook: rrr={Rrr} status={Status} type={Type}",
                    callback.Rrr, callback.Status, callback.NotificationType);

                return Task.FromResult(MapWebhookEvent(callback));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Remita webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(RemitaWebhookEvent callback)
    {
        var rrr = callback.Rrr ?? callback.OrderId ?? string.Empty;
        if (string.IsNullOrEmpty(rrr)) return null;

        var rawStatus = callback.Status?.ToLowerInvariant();
        var notificationType = callback.NotificationType?.ToLowerInvariant();
        var amount = callback.Amount;
        var currency = callback.Currency ?? "NGN";

        // Mandate notifications carry an explicit notificationType prefix.
        if (notificationType is not null)
        {
            switch (notificationType)
            {
                case "mandate.activated":
                case "mandate.active":
                    return new MandateActivatedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Completed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.MandateActivated,
                        MandateReference = callback.MandateId ?? rrr,
                        AmountLimit = amount > 0 ? amount : null,
                        Currency = currency
                    };

                case "mandate.cancelled":
                case "mandate.canceled":
                case "mandate.terminated":
                    return new MandateCancelledEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Cancelled,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.MandateCancelled,
                        MandateReference = callback.MandateId ?? rrr,
                        CancellationReason = callback.CancellationReason
                    };

                case "payout.successful":
                case "transfer.successful":
                    return new PayoutCompletedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Completed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = callback.TransRef ?? rrr,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = callback.CreditAccount
                    };

                case "payout.failed":
                case "transfer.failed":
                    return new PayoutFailedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Failed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = callback.TransRef ?? rrr,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = callback.Status,
                        FailureMessage = callback.Message
                    };

                case "settlement.completed":
                    return new SettlementCompletedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Completed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.SettlementCompleted,
                        SettlementReference = rrr,
                        NetAmount = amount,
                        Currency = currency
                    };
            }
        }

        // Otherwise fall through to status-based mapping (Remita's classic e-collection callback).
        switch (rawStatus)
        {
            case "00":
            case "01":
            case "success":
            case "successful":
            case "completed":
                return new ChargeSucceededEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Completed,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency
                };

            case "021":
            case "025":
            case "pending":
                return new ChargePendingEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Pending,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                };

            case "020":
            case "failed":
            case "declined":
                return new ChargeFailedEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Failed,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = callback.Status,
                    FailureMessage = callback.Message
                };

            case "refunded":
                return new RefundSucceededEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Refunded,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = rrr,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            default:
                return null;
        }
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation, string authHeader)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Remita failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Remita {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static PaymentStatus MapStatusCode(string? code) => code?.ToLowerInvariant() switch
    {
        "00" or "01" or "025" => PaymentStatus.Pending,            // Remita: 025=PaymentInitiated.
        "0" or "success" or "successful" or "completed" => PaymentStatus.Completed,
        "020" or "failed" or "declined" => PaymentStatus.Failed,
        "021" or "pending" => PaymentStatus.Pending,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Remita API response shapes (internal) ===

    private sealed class RemitaPaymentInitResponse
    {
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
        [JsonPropertyName("RRR")] public string? Rrr { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class RemitaRefundResponse
    {
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
    }

    private sealed class RemitaSendMoneyResponse
    {
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("transRef")] public string? TransRef { get; set; }
    }

    private sealed class RemitaWebhookEvent
    {
        [JsonPropertyName("rrr")] public string? Rrr { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("notificationType")] public string? NotificationType { get; set; }
        [JsonPropertyName("mandateId")] public string? MandateId { get; set; }
        [JsonPropertyName("transRef")] public string? TransRef { get; set; }
        [JsonPropertyName("creditAccount")] public string? CreditAccount { get; set; }
        [JsonPropertyName("cancellationReason")] public string? CancellationReason { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private static readonly JsonSerializerOptions s_caseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
