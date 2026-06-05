// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Moniepoint.Providers;

/// <summary>
/// Moniepoint (Nigeria) payment gateway provider. Wraps the Moniepoint REST API
/// for initialised checkout, verify, refund and transfer operations.
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class MoniepointPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly MoniepointOptions _options;
    private readonly MoniepointIdempotencyCache? _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Moniepoint;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public MoniepointPaymentProvider(
        HttpClient httpClient,
        IOptions<MoniepointOptions> options,
        ILogger<MoniepointPaymentProvider> logger,
        MoniepointIdempotencyCache? idempotency = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MoniepointOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.moniepoint.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    /// <inheritdoc />
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "charge",
                () => RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var reference = request.Metadata?.GetValueOrDefault("reference") ?? request.IdempotencyKey ?? $"mpt-{Guid.NewGuid():N}";
        var customerEmail = request.Metadata?.GetValueOrDefault("email") ?? "noreply@bhengu.example";
        var customerName = request.Metadata?.GetValueOrDefault("name") ?? "Bhengu Customer";
        var customerPhone = request.Metadata?.GetValueOrDefault("phone") ?? string.Empty;

        var requestBody = new
        {
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            reference,
            customer = new { email = customerEmail, name = customerName, phone = customerPhone },
            redirectUrl = _options.RedirectUrl,
            paymentMethod = request.PaymentMethodToken
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/transactions/initialize",
            requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointInitResponse>(body);

        Logger.LogInformation("Moniepoint init: {Reference} status={Status}",
            resp?.Data?.Reference ?? reference, resp?.Data?.Status);

        var status = MapStatus(resp?.Data?.Status);
        return new PaymentResponse
        {
            GatewayReference = resp?.Data?.Reference ?? reference,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    /// <inheritdoc />
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "refund",
                () => RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var requestBody = new
        {
            amount = request.Amount,
            reason = request.Reason
        };

        var path = $"api/v1/transactions/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointRefundResponse>(body);

        Logger.LogInformation("Moniepoint refund created: {RefundRef} for txn {TxnRef}",
            resp?.Data?.RefundReference, request.GatewayReference);

        var status = MapStatus(resp?.Data?.Status);
        return new RefundResponse
        {
            GatewayReference = resp?.Data?.RefundReference ?? string.Empty,
            Amount = request.Amount,
            Status = status,
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.Message
        };
    }

    /// <inheritdoc />
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "payout",
                () => RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        // DestinationToken expected format: "<bankCode>:<accountNumber>" or "<accountNumber>".
        string beneficiaryBank = string.Empty;
        string beneficiaryAccount = request.DestinationToken;
        var sep = request.DestinationToken.IndexOf(':');
        if (sep > 0)
        {
            beneficiaryBank = request.DestinationToken[..sep];
            beneficiaryAccount = request.DestinationToken[(sep + 1)..];
        }

        var requestBody = new
        {
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            beneficiaryAccount,
            beneficiaryBank,
            narration = request.Description,
            reference = request.IdempotencyKey ?? $"tfr-{Guid.NewGuid():N}"
        };

        var body = await SendAsync(HttpMethod.Post, "api/v1/transfers", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<MoniepointTransferResponse>(body);

        Logger.LogInformation("Moniepoint transfer created: {Reference} status={Status}",
            resp?.Data?.Reference, resp?.Data?.Status);

        var status = MapStatus(resp?.Data?.Status);
        return new PayoutResponse
        {
            GatewayReference = resp?.Data?.Reference ?? string.Empty,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.ApiKey : _options.WebhookSecret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                Logger.LogWarning("Moniepoint webhook secret not configured — signature verification cannot succeed.");
                return false;
            }

            return SignatureHelpers.VerifyHmacSha256(payload, signature, secret);
        });
    }

    /// <inheritdoc />
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        return RunOperationAsync<WebhookEvent?>("parse_webhook", () =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<MoniepointWebhookEvent>(payload);
                if (evt is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Moniepoint webhook event: {EventType}", evt.Event);
                var typed = MapWebhookEvent(evt);
                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Moniepoint webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(MoniepointWebhookEvent webhookEvent)
    {
        var eventType = webhookEvent.Event?.ToLowerInvariant() ?? string.Empty;
        var data = webhookEvent.Data;
        var rawReference = data?.Reference;
        if (string.IsNullOrEmpty(rawReference)) return null;

        var amount = data?.Amount ?? 0m;
        var currency = data?.Currency ?? "NGN";

        switch (eventType)
        {
            case "transaction.successful":
            case "transaction.success":
            case "charge.success":
                return new ChargeSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = data?.CustomerEmail,
                    PaymentMethodToken = data?.PaymentMethod
                };

            case "transaction.failed":
            case "charge.failed":
                return new ChargeFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.FailureReason
                };

            case "transaction.pending":
                return new ChargePendingEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                };

            case "refund.successful":
            case "refund.processed":
                return new RefundSucceededEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = data?.RefundReference ?? rawReference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "refund.failed":
                return new RefundFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.FailureReason
                };

            case "transfer.successful":
            case "transfer.success":
            case "payout.successful":
                return new PayoutCompletedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = rawReference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = data?.BeneficiaryAccount
                };

            case "transfer.failed":
            case "payout.failed":
                return new PayoutFailedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = rawReference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data?.Status,
                    FailureMessage = data?.FailureReason
                };

            case "settlement.completed":
            case "settlement.processed":
                return new SettlementCompletedEvent
                {
                    GatewayReference = rawReference,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = rawReference,
                    NetAmount = amount,
                    Currency = currency
                };

            default:
                // Unrecognised upstream event type — return null so the consumer's handler can
                // short-circuit cleanly instead of dispatching on a Category=Unknown placeholder.
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

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Moniepoint {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "successful" or "success" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" or "refund_processed" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Moniepoint API response shapes (internal) ===

    private sealed class MoniepointInitResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public MoniepointInitData? Data { get; set; }
    }

    private sealed class MoniepointInitData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("checkoutUrl")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class MoniepointRefundResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public MoniepointRefundData? Data { get; set; }
    }

    private sealed class MoniepointRefundData
    {
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class MoniepointTransferResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public MoniepointTransferData? Data { get; set; }
    }

    private sealed class MoniepointTransferData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class MoniepointWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public MoniepointWebhookData? Data { get; set; }
    }

    private sealed class MoniepointWebhookData
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("customerEmail")] public string? CustomerEmail { get; set; }
        [JsonPropertyName("paymentMethod")] public string? PaymentMethod { get; set; }
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("beneficiaryAccount")] public string? BeneficiaryAccount { get; set; }
        [JsonPropertyName("failureReason")] public string? FailureReason { get; set; }
    }
}
