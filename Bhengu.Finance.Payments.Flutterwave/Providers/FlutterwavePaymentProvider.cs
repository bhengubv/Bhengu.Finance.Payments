// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

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
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Flutterwave.Providers;

/// <summary>
/// Flutterwave pan-African payment provider. Wraps the Flutterwave v3 REST API and supports
/// payment initialisation (<c>/v3/payments</c>), transfers (<c>/v3/transfers</c>) for payouts,
/// and refunds. Webhook authenticity is checked via constant-time comparison of the
/// <c>verif-hash</c> header against the configured WebhookSecret (Flutterwave does not HMAC).
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public documentation; never sandbox-verified.")]
public sealed class FlutterwavePaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;
    private readonly FlutterwaveIdempotencyCache _idempotencyCache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Flutterwave;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.TypedWebhooks |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Marketplace |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.Disputes;

    /// <summary>
    /// Construct a Flutterwave provider bound to the supplied <paramref name="httpClient"/>.
    /// Sets the bearer-token Authorization header and the base address (defaults to
    /// <c>https://api.flutterwave.com/</c>) if not already populated.
    /// </summary>
    public FlutterwavePaymentProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwavePaymentProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotencyCache = new FlutterwaveIdempotencyCache();

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FlutterwaveOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.flutterwave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency,
            () => _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct)),
            ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var email = request.Metadata?.GetValueOrDefault("email");
        if (string.IsNullOrWhiteSpace(email))
            throw new PaymentDeclinedException(ProviderName, "missing_email",
                "Flutterwave requires an 'email' in PaymentRequest.Metadata.");

        var name = request.Metadata?.GetValueOrDefault("name") ?? email;
        var phone = request.Metadata?.GetValueOrDefault("phonenumber");

        // Optional metadata-driven extensions: payment_plan (subscription binding), subaccounts (splits).
        var paymentPlan = request.Metadata?.GetValueOrDefault("payment_plan");

        var requestBody = new Dictionary<string, object?>
        {
            ["tx_ref"] = request.PaymentMethodToken,
            ["amount"] = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            ["currency"] = request.Currency.ToUpperInvariant(),
            ["redirect_url"] = _options.RedirectUrl,
            ["customer"] = new
            {
                email,
                name,
                phonenumber = phone
            },
            ["customizations"] = new
            {
                title = request.Description
            }
        };
        if (!string.IsNullOrWhiteSpace(paymentPlan))
            requestBody["payment_plan"] = paymentPlan;

        var body = await SendAsync(HttpMethod.Post, "v3/payments", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var fwResponse = JsonSerializer.Deserialize<FlutterwavePaymentResponse>(body);

        Logger.LogInformation("Flutterwave payment initialised: {TxRef} status={Status}",
            request.PaymentMethodToken, fwResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = request.PaymentMethodToken,
            Status = MapStatus(fwResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = fwResponse?.Data?.Link,
            Message = fwResponse?.Message
        };
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency,
            () => _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, () => ProcessPayoutCoreAsync(request, ct)),
            ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        // DestinationToken format: "<bankCode>:<accountNumber>" (e.g. "044:0690000040").
        var colon = request.DestinationToken.IndexOf(':');
        if (colon <= 0)
            throw new PaymentDeclinedException(ProviderName, "invalid_destination",
                "Flutterwave PayoutRequest.DestinationToken must be 'bankCode:accountNumber'.");

        var bankCode = request.DestinationToken[..colon];
        var accountNumber = request.DestinationToken[(colon + 1)..];

        var reference = $"transfer-{Guid.NewGuid():N}";
        var requestBody = new
        {
            account_bank = bankCode,
            account_number = accountNumber,
            amount = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            narration = request.Description,
            currency = request.Currency.ToUpperInvariant(),
            reference,
            beneficiary_name = request.Description
        };

        var body = await SendAsync(HttpMethod.Post, "v3/transfers", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var transferResponse = JsonSerializer.Deserialize<FlutterwaveTransferResponse>(body);

        Logger.LogInformation("Flutterwave transfer initialised: {Reference} status={Status}",
            transferResponse?.Data?.Reference ?? reference, transferResponse?.Data?.Status);

        return new PayoutResponse
        {
            GatewayReference = transferResponse?.Data?.Reference ?? reference,
            Status = MapStatus(transferResponse?.Data?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference,
            () => _idempotencyCache.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct)),
            ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var requestBody = new
        {
            amount = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
        };

        // request.GatewayReference is expected to be the Flutterwave transaction id (numeric).
        var path = $"v3/transactions/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<FlutterwaveRefundResponse>(body);

        Logger.LogInformation("Flutterwave refund created: {RefundId} for transaction {TransactionId}",
            refundResponse?.Data?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Data?.Id ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refundResponse?.Data?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Message
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            Logger.LogWarning("Flutterwave WebhookSecret not configured — signature verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        // Flutterwave does NOT HMAC the body; it sends the configured secret verbatim in the
        // verif-hash header. SignatureHelpers.ConstantTimeEquals defeats timing-based equality leaks.
        return RunWebhookVerify(() => SignatureHelpers.ConstantTimeEquals(signature, _options.WebhookSecret));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Returns a strongly-typed sub-record where the Flutterwave event maps cleanly onto one of the
    /// SDK's <see cref="WebhookEventCategory"/> values. Mapping table:
    /// <list type="bullet">
    /// <item><c>charge.completed</c> + <c>status=successful</c> → <see cref="ChargeSucceededEvent"/></item>
    /// <item><c>charge.completed</c> + <c>status=failed</c>     → <see cref="ChargeFailedEvent"/></item>
    /// <item><c>transfer.completed</c> + <c>status=SUCCESSFUL</c> → <see cref="PayoutCompletedEvent"/></item>
    /// <item><c>transfer.completed</c> + <c>status=FAILED</c>    → <see cref="PayoutFailedEvent"/></item>
    /// <item><c>subscription.cancelled</c> → <see cref="SubscriptionCancelledEvent"/></item>
    /// </list>
    /// Anything else returns the base <see cref="WebhookEvent"/> with
    /// <see cref="WebhookEventCategory.Unknown"/>. Returns null only when the payload is unparseable.
    /// </remarks>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload, ct), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<FlutterwaveWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Flutterwave webhook event: {EventType}", webhookEvent.Event);

            var reference = webhookEvent.Data?.TxRef ?? webhookEvent.Data?.Reference;
            if (string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            var eventName = webhookEvent.Event?.ToLowerInvariant();
            var innerStatus = webhookEvent.Data?.Status?.ToLowerInvariant();
            var currency = webhookEvent.Data?.Currency ?? string.Empty;
            var amount = webhookEvent.Data?.Amount ?? 0m;

            WebhookEvent? mapped = (eventName, innerStatus) switch
            {
                ("charge.completed" or "charge.complete", "successful" or "success" or "completed") =>
                    new ChargeSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = webhookEvent.Event,
                        Category = WebhookEventCategory.ChargeSucceeded,
                        Amount = amount,
                        Currency = currency,
                        CustomerId = webhookEvent.Data?.Customer?.Email,
                        PaymentMethodToken = webhookEvent.Data?.Card?.Token
                    },

                ("charge.completed" or "charge.complete", "failed" or "abandoned") =>
                    new ChargeFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = webhookEvent.Event,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = webhookEvent.Data?.ProcessorResponse,
                        FailureMessage = webhookEvent.Data?.Narration
                    },

                ("charge.failed", _) =>
                    new ChargeFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = webhookEvent.Event,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = webhookEvent.Data?.ProcessorResponse,
                        FailureMessage = webhookEvent.Data?.Narration
                    },

                ("transfer.completed", "successful" or "success") =>
                    new PayoutCompletedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = webhookEvent.Event,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = webhookEvent.Data?.AccountNumber
                    },

                ("transfer.completed", "failed") or ("transfer.failed", _) =>
                    new PayoutFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = webhookEvent.Event,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = webhookEvent.Data?.CompleteMessage,
                        FailureMessage = webhookEvent.Data?.Narration
                    },

                ("subscription.cancelled", _) =>
                    new SubscriptionCancelledEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Cancelled,
                        EventType = webhookEvent.Event,
                        Category = WebhookEventCategory.SubscriptionCancelled,
                        SubscriptionReference = reference,
                        CancellationReason = webhookEvent.Data?.Status
                    },

                _ => new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = MapStatus(innerStatus ?? "pending"),
                    EventType = webhookEvent.Event,
                    Category = WebhookEventCategory.Unknown
                }
            };

            return Task.FromResult<WebhookEvent?>(mapped);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Flutterwave webhook event");
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
            Logger.LogError("Flutterwave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "new" => PaymentStatus.Completed,
        "pending" or "processing" or "initialised" => PaymentStatus.Pending,
        "failed" or "abandoned" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Flutterwave API response shapes (internal) ===

    private sealed class FlutterwavePaymentResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwavePaymentData? Data { get; set; }
    }

    private sealed class FlutterwavePaymentData
    {
        [JsonPropertyName("link")] public string? Link { get; set; }
    }

    private sealed class FlutterwaveTransferResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwaveTransferData? Data { get; set; }
    }

    private sealed class FlutterwaveTransferData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class FlutterwaveRefundResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwaveRefundData? Data { get; set; }
    }

    private sealed class FlutterwaveRefundData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount_refunded")] public decimal AmountRefunded { get; set; }
    }

    private sealed class FlutterwaveWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public FlutterwaveWebhookData? Data { get; set; }
    }

    private sealed class FlutterwaveWebhookData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tx_ref")] public string? TxRef { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("processor_response")] public string? ProcessorResponse { get; set; }
        [JsonPropertyName("narration")] public string? Narration { get; set; }
        [JsonPropertyName("complete_message")] public string? CompleteMessage { get; set; }
        [JsonPropertyName("account_number")] public string? AccountNumber { get; set; }
        [JsonPropertyName("customer")] public FlutterwaveWebhookCustomer? Customer { get; set; }
        [JsonPropertyName("card")] public FlutterwaveWebhookCard? Card { get; set; }
    }

    private sealed class FlutterwaveWebhookCustomer
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class FlutterwaveWebhookCard
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("first_6digits")] public string? First6Digits { get; set; }
        [JsonPropertyName("last_4digits")] public string? Last4Digits { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("expiry")] public string? Expiry { get; set; }
    }
}
