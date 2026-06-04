// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco (South Africa) payment gateway provider. Wraps the Yoco Online REST API.
/// Yoco does NOT expose payouts on the standard merchant API — <see cref="IPayoutProvider"/>
/// is intentionally not implemented; merchants requiring payouts should use Yoco Business/Marketplace.
/// </summary>
public sealed class YocoPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Yoco;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.TypedWebhooks |
        ProviderCapabilities.SyncSettlement |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.ThreeDSecure;

    public YocoPaymentProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoPaymentProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var amountInCents = (int)(request.Amount * 100);

        var requestBody = new
        {
            token = request.PaymentMethodToken,
            amountInCents,
            currency = request.Currency.ToUpperInvariant(),
            metadata = request.Metadata ?? new Dictionary<string, string>().AsReadOnly() as IReadOnlyDictionary<string, string>
        };

        var body = await SendAsync(HttpMethod.Post, "charges/", requestBody, ct, "ProcessPayment").ConfigureAwait(false);

        var yocoResponse = JsonSerializer.Deserialize<YocoChargeResponse>(body, s_jsonOptions);

        Logger.LogInformation("Yoco charge created: {ChargeId} status={Status}", yocoResponse?.Id, yocoResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = yocoResponse?.Id ?? string.Empty,
            Status = MapStatus(yocoResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = yocoResponse?.Status
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
        var amountInCents = (int)(request.Amount * 100);
        var requestBody = new
        {
            chargeId = request.GatewayReference,
            amountInCents
        };

        var body = await SendAsync(HttpMethod.Post, "refunds/", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<YocoRefundResponse>(body, s_jsonOptions);

        Logger.LogInformation("Yoco refund created: {RefundId} for charge {ChargeId}",
            refundResponse?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Id ?? string.Empty,
            Amount = request.Amount,
            Status = MapStatus(refundResponse?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            {
                Logger.LogWarning("Yoco WebhookSecret not configured — signature verification cannot succeed.");
                return false;
            }

            return SignatureHelpers.VerifyHmacSha256(payload, signature, _options.WebhookSecret, SignatureHelpers.Encoding.Base64);
        });
    }

    /// <summary>
    /// Parse a Yoco webhook payload into a typed <see cref="WebhookEvent"/> sub-record.
    /// </summary>
    /// <remarks>
    /// Mapping rules (Yoco webhook <c>type</c>):
    /// <list type="bullet">
    /// <item><description><c>payment.succeeded</c> / <c>charge.succeeded</c> → <see cref="ChargeSucceededEvent"/>.</description></item>
    /// <item><description><c>payment.failed</c> / <c>charge.failed</c> → <see cref="ChargeFailedEvent"/>.</description></item>
    /// <item><description><c>refund.succeeded</c> → <see cref="RefundSucceededEvent"/>.</description></item>
    /// <item><description><c>refund.failed</c> → <see cref="RefundFailedEvent"/>.</description></item>
    /// <item><description><c>payout.completed</c> → <see cref="PayoutCompletedEvent"/>.</description></item>
    /// <item><description><c>payout.failed</c> → <see cref="PayoutFailedEvent"/>.</description></item>
    /// </list>
    /// </remarks>
    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync<WebhookEvent?>("parse_webhook", () => ParseWebhookCoreAsync(payload), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload)
    {
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<YocoWebhookEvent>(payload, s_jsonOptions);
            if (webhookEvent is null || string.IsNullOrEmpty(webhookEvent.Payload?.Id))
                return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Yoco webhook event: {EventType}", webhookEvent.Type);

            var type = webhookEvent.Type?.ToLowerInvariant() ?? string.Empty;
            var id = webhookEvent.Payload.Id;
            var amount = webhookEvent.Payload.AmountInCents / 100m;
            var currency = (webhookEvent.Payload.Currency ?? "ZAR").ToUpperInvariant();

            WebhookEvent typed = type switch
            {
                "payment.succeeded" or "charge.succeeded" => new ChargeSucceededEvent
                {
                    GatewayReference = id,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = webhookEvent.Payload.CustomerId,
                    PaymentMethodToken = webhookEvent.Payload.CardId
                },
                "payment.failed" or "charge.failed" => new ChargeFailedEvent
                {
                    GatewayReference = id,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.Payload.FailureCode,
                    FailureMessage = webhookEvent.Payload.FailureMessage
                },
                "refund.succeeded" => new RefundSucceededEvent
                {
                    GatewayReference = webhookEvent.Payload.ChargeId ?? id,
                    Status = PaymentStatus.Refunded,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = id,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = webhookEvent.Payload.IsPartial
                },
                "refund.failed" => new RefundFailedEvent
                {
                    GatewayReference = webhookEvent.Payload.ChargeId ?? id,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.Payload.FailureCode,
                    FailureMessage = webhookEvent.Payload.FailureMessage
                },
                "payout.completed" => new PayoutCompletedEvent
                {
                    GatewayReference = id,
                    Status = PaymentStatus.Completed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = id,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = webhookEvent.Payload.BankAccountId
                },
                "payout.failed" => new PayoutFailedEvent
                {
                    GatewayReference = id,
                    Status = PaymentStatus.Failed,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = id,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.Payload.FailureCode,
                    FailureMessage = webhookEvent.Payload.FailureMessage
                },
                _ => new WebhookEvent
                {
                    GatewayReference = id,
                    Status = PaymentStatus.Pending,
                    EventType = webhookEvent.Type,
                    Category = WebhookEventCategory.Unknown
                }
            };

            return Task.FromResult<WebhookEvent?>(typed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Failed to parse Yoco webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, s_jsonOptions);
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
            Logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "successful" or "succeeded" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Yoco API response shapes (internal) ===

    private sealed class YocoChargeResponse
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
        public int AmountInCents { get; set; }
        public string? Currency { get; set; }
    }

    private sealed class YocoRefundResponse
    {
        public string? Id { get; set; }
        public string? ChargeId { get; set; }
        public int AmountInCents { get; set; }
        public string? Status { get; set; }
    }

    private sealed class YocoWebhookEvent
    {
        public string? Type { get; set; }
        public YocoWebhookPayload? Payload { get; set; }
    }

    private sealed class YocoWebhookPayload
    {
        public string? Id { get; set; }
        public string? ChargeId { get; set; }
        public string? Status { get; set; }
        public int AmountInCents { get; set; }
        public string? Currency { get; set; }
        public string? CustomerId { get; set; }
        public string? CardId { get; set; }
        public string? BankAccountId { get; set; }
        public string? FailureCode { get; set; }
        public string? FailureMessage { get; set; }
        public bool IsPartial { get; set; }
    }
}
