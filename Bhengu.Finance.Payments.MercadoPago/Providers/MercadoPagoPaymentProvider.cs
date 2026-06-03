// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MercadoPago.Providers;

/// <summary>
/// Mercado Pago (Latin America) payment gateway provider. Wraps the Mercado Pago REST API
/// (<c>https://api.mercadopago.com</c>) for card, PIX, boleto and wallet payments, refunds and money-out payouts.
/// PIX charges return the QR code + copy-paste string under <c>point_of_interaction.transaction_data</c>.
/// </summary>
public sealed class MercadoPagoPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly MercadoPagoOptions _options;
    private readonly ILogger<MercadoPagoPaymentProvider> _logger;

    public string ProviderName => ProviderNames.MercadoPago;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Subscriptions |
        ProviderCapabilities.QrCode |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.TypedWebhooks |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Idempotency;

    public MercadoPagoPaymentProvider(
        HttpClient httpClient,
        IOptions<MercadoPagoOptions> options,
        ILogger<MercadoPagoPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MercadoPagoOptions.AccessToken)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mercadopago.com");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, string>();
        var paymentMethodId = metadata.TryGetValue("payment_method_id", out var pmid) ? pmid : "visa";
        var payerEmail = metadata.TryGetValue("payer_email", out var pe) ? pe : metadata.GetValueOrDefault("email");

        if (string.IsNullOrWhiteSpace(payerEmail))
            throw new PaymentDeclinedException(ProviderName, "missing_payer_email",
                "Mercado Pago requires a 'payer_email' (or 'email') in PaymentRequest.Metadata.");

        // PIX and boleto do not use a tokenised card; cards do.
        var isCardlessMethod = paymentMethodId.Equals("pix", StringComparison.OrdinalIgnoreCase)
            || paymentMethodId.StartsWith("bol", StringComparison.OrdinalIgnoreCase);

        var requestBody = new Dictionary<string, object?>
        {
            ["transaction_amount"] = request.Amount,
            ["description"] = request.Description,
            ["payment_method_id"] = paymentMethodId,
            ["installments"] = metadata.TryGetValue("installments", out var inst) && int.TryParse(inst, out var i) ? i : 1,
            ["notification_url"] = _options.NotificationUrl,
            ["payer"] = new Dictionary<string, object?>
            {
                ["email"] = payerEmail,
                ["first_name"] = metadata.GetValueOrDefault("first_name"),
                ["last_name"] = metadata.GetValueOrDefault("last_name"),
                ["identification"] = new
                {
                    type = metadata.GetValueOrDefault("identification_type") ?? "CPF",
                    number = metadata.GetValueOrDefault("identification_number")
                }
            }
        };

        if (!isCardlessMethod)
            requestBody["token"] = request.PaymentMethodToken;

        var (body, _) = await SendAsync(HttpMethod.Post, "/v1/payments", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var mpResponse = JsonSerializer.Deserialize<MercadoPagoPaymentResponse>(body);

        _logger.LogInformation("Mercado Pago payment created: {PaymentId} status={Status} method={Method}",
            mpResponse?.Id, mpResponse?.Status, paymentMethodId);

        return new PaymentResponse
        {
            GatewayReference = mpResponse?.Id?.ToString() ?? string.Empty,
            Status = MapStatus(mpResponse?.Status ?? "pending"),
            Amount = mpResponse?.TransactionAmount ?? request.Amount,
            Currency = mpResponse?.CurrencyId ?? request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = mpResponse?.StatusDetail ?? mpResponse?.Status
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mercado Pago: POST /v1/payments/{id}/refunds. Omit amount for full refund.
        var requestBody = new { amount = request.Amount };

        var (body, _) = await SendAsync(
            HttpMethod.Post,
            $"/v1/payments/{request.GatewayReference}/refunds",
            requestBody,
            ct,
            "ProcessRefund").ConfigureAwait(false);

        var refundResponse = JsonSerializer.Deserialize<MercadoPagoRefundResponse>(body);

        _logger.LogInformation("Mercado Pago refund created: {RefundId} for payment {PaymentId}",
            refundResponse?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Id?.ToString() ?? string.Empty,
            Amount = refundResponse?.Amount ?? request.Amount,
            Status = MapStatus(refundResponse?.Status ?? "approved"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mercado Pago money-out: POST /v1/money_requests
        var requestBody = new
        {
            amount = request.Amount,
            currency_id = request.Currency,
            description = request.Description,
            payer_id = _options.AccessToken,
            payee = new
            {
                email = request.DestinationToken,
                identification = new { type = "CPF", number = string.Empty }
            }
        };

        var (body, _) = await SendAsync(HttpMethod.Post, "/v1/money_requests", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var payoutResponse = JsonSerializer.Deserialize<MercadoPagoPayoutResponse>(body);

        _logger.LogInformation("Mercado Pago payout created: {PayoutId} status={Status}",
            payoutResponse?.Id, payoutResponse?.Status);

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.Id?.ToString() ?? string.Empty,
            Status = MapStatus(payoutResponse?.Status ?? "pending"),
            Amount = payoutResponse?.Amount ?? request.Amount,
            Currency = payoutResponse?.CurrencyId ?? request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Verify a Mercado Pago webhook signature. The <paramref name="signature"/> argument is the raw
    /// <c>x-signature</c> header value, e.g. <c>ts=1701390000,v1=abcdef...</c>. The <paramref name="payload"/>
    /// argument is expected to be the manifest string Mercado Pago documents:
    /// <c>id:&lt;data.id&gt;;request-id:&lt;x-request-id&gt;;ts:&lt;ts&gt;;</c>. The merchant constructs that
    /// manifest from the relevant headers and the body before calling here.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Mercado Pago WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            // Extract v1=... from the header
            var v1Part = signature.Split(',')
                .Select(p => p.Trim())
                .FirstOrDefault(p => p.StartsWith("v1=", StringComparison.OrdinalIgnoreCase));

            if (v1Part is null) return false;
            var providedHash = v1Part["v1=".Length..];

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedHash.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Mercado Pago webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<MercadoPagoWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Mercado Pago webhook event: {Type} action={Action}",
                webhookEvent.Type, webhookEvent.Action);

            var typed = MapTypedEvent(webhookEvent);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Mercado Pago webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // Map Mercado Pago (topic, action) → strongly-typed Bhengu webhook sub-record. Returns the
    // base WebhookEvent for legacy "payment.approved" topic shapes so existing consumers don't regress.
    private static WebhookEvent? MapTypedEvent(MercadoPagoWebhookEvent evt)
    {
        var topic = evt.Type?.ToLowerInvariant();
        var action = evt.Action?.ToLowerInvariant();
        var dataId = evt.Data?.Id;
        var status = evt.Data?.Status?.ToLowerInvariant();
        var amount = evt.Data?.TransactionAmount ?? 0m;
        var currency = evt.Data?.CurrencyId ?? "BRL";

        if (string.IsNullOrEmpty(dataId))
            return null;

        switch (topic)
        {
            case "payment":
                if (action == "payment.created")
                    return new ChargePendingEvent
                    {
                        GatewayReference = dataId,
                        Status = PaymentStatus.Pending,
                        EventType = evt.Action,
                        Category = WebhookEventCategory.ChargePending,
                        Amount = amount,
                        Currency = currency
                    };

                if (action == "payment.updated" || action == "payment.approved")
                {
                    // payment.approved is its own terminal signal regardless of whether the body
                    // carries a status field (older webhook contract did not).
                    if (action == "payment.approved" || status == "approved")
                        return new ChargeSucceededEvent
                        {
                            GatewayReference = dataId,
                            Status = PaymentStatus.Completed,
                            EventType = evt.Action,
                            Category = WebhookEventCategory.ChargeSucceeded,
                            Amount = amount,
                            Currency = currency
                        };

                    if (status == "rejected" || status == "failed")
                        return new ChargeFailedEvent
                        {
                            GatewayReference = dataId,
                            Status = PaymentStatus.Failed,
                            EventType = evt.Action,
                            Category = WebhookEventCategory.ChargeFailed,
                            Amount = amount,
                            Currency = currency,
                            FailureCode = evt.Data?.StatusDetail
                        };

                    if (status == "refunded" || status == "charged_back")
                        return new RefundSucceededEvent
                        {
                            GatewayReference = dataId,
                            Status = PaymentStatus.Refunded,
                            EventType = evt.Action,
                            Category = WebhookEventCategory.RefundSucceeded,
                            RefundReference = dataId,
                            Amount = amount,
                            Currency = currency,
                            IsPartial = false
                        };

                    // Fallback: payment.updated with an unmapped status — still surface the event.
                    return new WebhookEvent
                    {
                        GatewayReference = dataId,
                        Status = MapStatus(status ?? string.Empty),
                        EventType = evt.Action,
                        Category = WebhookEventCategory.Unknown
                    };
                }

                if (action == "payment.cancelled")
                    return new ChargeFailedEvent
                    {
                        GatewayReference = dataId,
                        Status = PaymentStatus.Cancelled,
                        EventType = evt.Action,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = "cancelled"
                    };
                break;

            case "refund":
                return new RefundSucceededEvent
                {
                    GatewayReference = dataId,
                    Status = PaymentStatus.Refunded,
                    EventType = evt.Action,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = dataId,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "preapproval":
                if (action == "updated" || action == "preapproval.updated")
                {
                    if (status == "cancelled" || status == "canceled")
                        return new SubscriptionCancelledEvent
                        {
                            GatewayReference = dataId,
                            Status = PaymentStatus.Cancelled,
                            EventType = evt.Action,
                            Category = WebhookEventCategory.SubscriptionCancelled,
                            SubscriptionReference = dataId,
                            CancellationReason = "cancelled"
                        };

                    if (status == "paused")
                        return new SubscriptionCancelledEvent
                        {
                            GatewayReference = dataId,
                            Status = PaymentStatus.Cancelled,
                            EventType = evt.Action,
                            Category = WebhookEventCategory.SubscriptionCancelled,
                            SubscriptionReference = dataId,
                            CancellationReason = "paused"
                        };
                }
                break;

            case "subscription_authorized_payment":
                return new SubscriptionRenewedEvent
                {
                    GatewayReference = dataId,
                    Status = PaymentStatus.Completed,
                    EventType = evt.Action ?? evt.Type,
                    Category = WebhookEventCategory.SubscriptionRenewed,
                    SubscriptionReference = dataId,
                    Amount = amount,
                    Currency = currency
                };
        }

        // Legacy approved-only topic shape kept for callers that have been relying on it.
        if (topic == "payment" && action == "payment.approved")
            return new ChargeSucceededEvent
            {
                GatewayReference = dataId,
                Status = PaymentStatus.Completed,
                EventType = evt.Action,
                Category = WebhookEventCategory.ChargeSucceeded,
                Amount = amount,
                Currency = currency
            };

        return null;
    }

    private async Task<(string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // Mercado Pago requires an X-Idempotency-Key on POST requests to prevent duplicate charges.
        if (method == HttpMethod.Post)
            req.Headers.Add("X-Idempotency-Key", Guid.NewGuid().ToString("N"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Mercado Pago failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Mercado Pago {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return (responseBody, response);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "approved" or "authorized" or "completed" => PaymentStatus.Completed,
        "pending" or "in_process" or "in_mediation" => PaymentStatus.Pending,
        "rejected" or "failed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" or "charged_back" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Mercado Pago API response shapes (internal) ===

    private sealed class MercadoPagoPaymentResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
        [JsonPropertyName("transaction_amount")] public decimal? TransactionAmount { get; set; }
        [JsonPropertyName("currency_id")] public string? CurrencyId { get; set; }
        [JsonPropertyName("payment_method_id")] public string? PaymentMethodId { get; set; }
    }

    private sealed class MercadoPagoRefundResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("payment_id")] public long? PaymentId { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class MercadoPagoPayoutResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("currency_id")] public string? CurrencyId { get; set; }
    }

    private sealed class MercadoPagoWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("data")] public MercadoPagoWebhookData? Data { get; set; }
        [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
    }

    private sealed class MercadoPagoWebhookData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
        [JsonPropertyName("transaction_amount")] public decimal? TransactionAmount { get; set; }
        [JsonPropertyName("currency_id")] public string? CurrencyId { get; set; }
    }
}
