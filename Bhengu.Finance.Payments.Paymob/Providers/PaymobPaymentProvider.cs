// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using System.Globalization;
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
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paymob.Providers;

/// <summary>
/// Paymob (Egypt + GCC + Pakistan) payment gateway provider. Wraps the Paymob Accept REST API
/// and the Paymob Disbursement API. <see cref="ProcessPaymentAsync"/> performs the full 4-step
/// Accept handshake: authenticate → create order → create payment key → return iframe URL/token.
/// </summary>
/// <remarks>
/// 3DS is signalled at payment-key creation time via the <c>request_3d_secure</c> flag on the
/// Accept payment-key API; explicit step-up flow is exposed via the sibling
/// <see cref="PaymobThreeDSecureProvider"/> for consumers that want to pre-flight authentication.
/// </remarks>
public sealed class PaymobPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaymobOptions _options;
    private readonly PaymobIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paymob;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.ThreeDSecure |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PaymobPaymentProvider(
        HttpClient httpClient,
        IOptions<PaymobOptions> options,
        ILogger<PaymobPaymentProvider> logger,
        PaymobIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaymobOptions.ApiKey)} is required");

        PaymobHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Currency);
        var sw = Stopwatch.StartNew();

        var integrationId = request.Metadata?.GetValueOrDefault("integration_id") is { Length: > 0 } iidStr
                && int.TryParse(iidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iid)
            ? iid
            : _options.IntegrationId;
        var iframeId = request.Metadata?.GetValueOrDefault("iframe_id") is { Length: > 0 } ifStr
                && int.TryParse(ifStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ifv)
            ? ifv
            : _options.IframeId;

        if (integrationId <= 0)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Error));
            throw new PaymentDeclinedException(ProviderName, "missing_integration_id",
                "Paymob requires an 'integration_id' in PaymentRequest.Metadata or PaymobOptions.IntegrationId.");
        }

        var amountCents = (long)(request.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();

        try
        {
            // 1. Authenticate
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);

            // 2. Create order
            var orderBody = new
            {
                auth_token = authToken,
                delivery_needed = false,
                amount_cents = amountCents,
                currency,
                items = Array.Empty<object>(),
                merchant_order_id = request.Metadata?.GetValueOrDefault("merchant_order_id")
            };
            var orderJson = await PaymobHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "api/ecommerce/orders", orderBody, "CreateOrder", ct).ConfigureAwait(false);
            var orderResponse = JsonSerializer.Deserialize<PaymobOrderResponse>(orderJson, PaymobHttpClient.Json);
            var orderId = orderResponse?.Id;
            if (orderId is null || orderId == 0)
                throw new ProviderUnavailableException(ProviderName, "Paymob order creation returned no id");

            // 3. Create payment key — request_3d_secure honours the per-request flag, defaulting on.
            var requestThreeDs = !string.Equals(request.Metadata?.GetValueOrDefault("request_3d_secure"), "false", StringComparison.OrdinalIgnoreCase);
            var billing = BuildBillingPayload(request);
            var keyBody = new
            {
                auth_token = authToken,
                amount_cents = amountCents,
                expiration = 3600,
                order_id = orderId,
                billing_data = billing,
                currency,
                integration_id = integrationId,
                lock_order_when_paid = true,
                request_3d_secure = requestThreeDs
            };
            var keyJson = await PaymobHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "api/acceptance/payment_keys", keyBody, "CreatePaymentKey", ct).ConfigureAwait(false);
            var keyResponse = JsonSerializer.Deserialize<PaymobPaymentKeyResponse>(keyJson, PaymobHttpClient.Json);
            var paymentKey = keyResponse?.Token;
            if (string.IsNullOrEmpty(paymentKey))
                throw new ProviderUnavailableException(ProviderName, "Paymob payment_keys returned no token");

            Logger.LogInformation("Paymob 4-step flow complete: order={OrderId} integration={IntegrationId} 3ds={ThreeDs}",
                orderId, integrationId, requestThreeDs);

            var iframeUrl = iframeId > 0
                ? $"https://accept.paymob.com/api/acceptance/iframes/{iframeId}?payment_token={paymentKey}"
                : paymentKey;

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Pending);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Pending));

            return new PaymentResponse
            {
                GatewayReference = orderId.Value.ToString(CultureInfo.InvariantCulture),
                Status = PaymentStatus.Pending,
                Amount = request.Amount,
                Currency = currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = iframeUrl
            };
        }
        catch (PaymentDeclinedException)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Declined);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Declined));
            throw;
        }
        catch (ProviderRateLimitException)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.RateLimited);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.RateLimited));
            throw;
        }
        catch (Exception)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Unavailable);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Unavailable));
            throw;
        }
        finally
        {
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderName));
        }
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderName, request.GatewayReference);
        try
        {
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
            var amountCents = (long)(request.Amount * 100);
            var refundBody = new
            {
                auth_token = authToken,
                transaction_id = request.GatewayReference,
                amount_cents = amountCents
            };

            var body = await PaymobHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "api/acceptance/void_refund/refund", refundBody, "ProcessRefund", ct).ConfigureAwait(false);
            var refund = JsonSerializer.Deserialize<PaymobTransactionResponse>(body, PaymobHttpClient.Json);

            Logger.LogInformation("Paymob refund completed for transaction {Transaction} success={Success}",
                request.GatewayReference, refund?.Success);

            var status = refund?.Success == true ? PaymentStatus.Refunded : PaymentStatus.Pending;
            activity.SetOutcome(status == PaymentStatus.Refunded ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending);
            BhenguPaymentDiagnostics.RefundsTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", status == PaymentStatus.Refunded ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending));

            return new RefundResponse
            {
                GatewayReference = refund?.Id?.ToString(CultureInfo.InvariantCulture) ?? request.GatewayReference,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = refund?.Success.ToString()
            };
        }
        catch (Exception)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            BhenguPaymentDiagnostics.RefundsTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Error));
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderName, request.Currency);
        try
        {
            var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);
            var amountCents = (long)(request.Amount * 100);
            var disbursementBody = new
            {
                auth_token = authToken,
                amount_cents = amountCents,
                currency = request.Currency.ToUpperInvariant(),
                destination = request.DestinationToken,
                description = request.Description
            };

            var body = await PaymobHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "api/disbursements/transactions", disbursementBody, "ProcessPayout", ct).ConfigureAwait(false);
            var disbursement = JsonSerializer.Deserialize<PaymobTransactionResponse>(body, PaymobHttpClient.Json);

            Logger.LogInformation("Paymob disbursement created id={Id} success={Success}",
                disbursement?.Id, disbursement?.Success);

            var status = disbursement?.Success == true ? PaymentStatus.Completed : PaymentStatus.Pending;
            activity.SetOutcome(status == PaymentStatus.Completed ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", status == PaymentStatus.Completed ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending));

            return new PayoutResponse
            {
                GatewayReference = disbursement?.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Amount = request.Amount,
                Currency = request.Currency,
                Status = status,
                ProcessedAt = DateTime.UtcNow
            };
        }
        catch (Exception)
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1, new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Error));
            throw;
        }
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        bool valid;
        if (string.IsNullOrWhiteSpace(_options.HmacSecret))
        {
            Logger.LogWarning("Paymob HmacSecret not configured — webhook signature verification cannot succeed.");
            valid = false;
        }
        else
        {
            try
            {
                using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.HmacSecret));
                var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
                var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

                valid = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                    Encoding.UTF8.GetBytes(computedSignature));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Paymob webhook signature verification raised");
                valid = false;
            }
        }

        BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("valid", valid));
        return valid;
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var callback = JsonSerializer.Deserialize<PaymobWebhookCallback>(payload, PaymobHttpClient.Json);
            if (callback?.Obj is null) return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Paymob webhook: type={Type} success={Success} id={Id}",
                callback.Type, callback.Obj.Success, callback.Obj.Id);

            var obj = callback.Obj;
            var amount = obj.AmountCents / 100m;
            var currency = obj.Currency ?? _options.Currency;
            var reference = obj.Id?.ToString(CultureInfo.InvariantCulture)
                ?? obj.Order?.Id?.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            // Type-discriminate: REFUND / DISBURSEMENT / TRANSACTION
            var typeUpper = callback.Type?.ToUpperInvariant();
            if (typeUpper == "DISBURSEMENT")
            {
                if (obj.Success == true)
                {
                    return Task.FromResult<WebhookEvent?>(new PayoutCompletedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = callback.Type,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = obj.Destination
                    });
                }
                return Task.FromResult<WebhookEvent?>(new PayoutFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = callback.Type,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = obj.Data?.Message ?? obj.ErrorOccured?.ToString(),
                    FailureMessage = obj.Data?.Message
                });
            }

            // Refund first — `is_refunded` overrides other flags.
            if (obj.IsRefunded == true)
            {
                return Task.FromResult<WebhookEvent?>(new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = callback.Type,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = obj.IsRefund == true && obj.Order?.AmountCents is long oa && obj.AmountCents > 0 && obj.AmountCents < oa
                });
            }

            // Successful charge
            if (obj.Success == true && obj.Pending != true && obj.IsVoided != true)
            {
                return Task.FromResult<WebhookEvent?>(new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = callback.Type,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = obj.Order?.MerchantOrderId,
                    PaymentMethodToken = obj.SourceData?.SubType
                });
            }

            if (obj.Pending == true)
            {
                return Task.FromResult<WebhookEvent?>(new ChargePendingEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Pending,
                    EventType = callback.Type,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                });
            }

            if (obj.Success == false)
            {
                return Task.FromResult<WebhookEvent?>(new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = callback.Type,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = obj.Data?.TxnResponseCode,
                    FailureMessage = obj.Data?.Message
                });
            }

            if (obj.IsVoided == true)
            {
                return Task.FromResult<WebhookEvent?>(new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Cancelled,
                    EventType = callback.Type,
                    Category = WebhookEventCategory.Unknown
                });
            }

            return Task.FromResult<WebhookEvent?>(null);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Paymob webhook callback");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static object BuildBillingPayload(PaymentRequest request) => new
    {
        email = request.Metadata?.GetValueOrDefault("email") ?? "na@na.na",
        first_name = request.Metadata?.GetValueOrDefault("first_name") ?? "NA",
        last_name = request.Metadata?.GetValueOrDefault("last_name") ?? "NA",
        phone_number = request.Metadata?.GetValueOrDefault("phone_number") ?? "+20000000000",
        apartment = "NA",
        floor = "NA",
        street = "NA",
        building = "NA",
        shipping_method = "NA",
        postal_code = "NA",
        city = "NA",
        country = "NA",
        state = "NA"
    };

    // === Paymob API response shapes (internal) ===

    private sealed class PaymobOrderResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("amount_cents")] public long AmountCents { get; set; }
    }

    private sealed class PaymobPaymentKeyResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
    }

    private sealed class PaymobTransactionResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("pending")] public bool? Pending { get; set; }
        [JsonPropertyName("is_refunded")] public bool? IsRefunded { get; set; }
        [JsonPropertyName("is_refund")] public bool? IsRefund { get; set; }
        [JsonPropertyName("is_voided")] public bool? IsVoided { get; set; }
        [JsonPropertyName("amount_cents")] public long AmountCents { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("destination")] public string? Destination { get; set; }
        [JsonPropertyName("error_occured")] public bool? ErrorOccured { get; set; }
        [JsonPropertyName("order")] public PaymobWebhookOrder? Order { get; set; }
        [JsonPropertyName("source_data")] public PaymobSourceData? SourceData { get; set; }
        [JsonPropertyName("data")] public PaymobWebhookData? Data { get; set; }
    }

    private sealed class PaymobWebhookOrder
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("merchant_order_id")] public string? MerchantOrderId { get; set; }
        [JsonPropertyName("amount_cents")] public long? AmountCents { get; set; }
    }

    private sealed class PaymobSourceData
    {
        [JsonPropertyName("pan")] public string? Pan { get; set; }
        [JsonPropertyName("sub_type")] public string? SubType { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class PaymobWebhookData
    {
        [JsonPropertyName("txn_response_code")] public string? TxnResponseCode { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class PaymobWebhookCallback
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("obj")] public PaymobTransactionResponse? Obj { get; set; }
    }
}
