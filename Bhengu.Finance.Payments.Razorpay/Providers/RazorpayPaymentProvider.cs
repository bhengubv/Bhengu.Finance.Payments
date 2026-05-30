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
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay (India) payment gateway provider. Wraps the Razorpay REST API and supports
/// the server-side capture of a pre-authorised payment_id from a client-side checkout,
/// refunds, and RazorpayX payouts.
/// </summary>
/// <remarks>
/// PaymentRequest.PaymentMethodToken is expected to be a Razorpay <c>payment_id</c>
/// returned by the Razorpay client-side checkout. The provider issues
/// <c>POST /v1/payments/{paymentId}/capture</c> to settle it.
/// To use the Orders flow instead, pass <c>"order"</c> as the <c>flow</c> key in Metadata
/// and the SDK will create an order and surface the order_id + checkout URL in the response.
/// </remarks>
public sealed class RazorpayPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly RazorpayOptions _options;
    private readonly ILogger<RazorpayPaymentProvider> _logger;

    public string ProviderName => ProviderNames.Razorpay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer;

    public RazorpayPaymentProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.KeyId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RazorpayOptions.KeyId)} is required");
        if (string.IsNullOrWhiteSpace(_options.KeySecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RazorpayOptions.KeySecret)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.razorpay.com/");

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.KeyId}:{_options.KeySecret}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInPaise = (long)(request.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? _options.Currency
            : request.Currency.ToUpperInvariant();

        var flow = request.Metadata?.GetValueOrDefault("flow")?.ToLowerInvariant();

        if (flow == "order")
        {
            // Orders flow — create a Razorpay order. Callers redirect the customer to the
            // hosted checkout with the returned order_id; settlement is later confirmed via webhook.
            var orderBody = new
            {
                amount = amountInPaise,
                currency,
                receipt = request.Metadata?.GetValueOrDefault("receipt") ?? $"rcpt_{Guid.NewGuid():N}",
                notes = request.Metadata ?? new Dictionary<string, string>().AsReadOnly() as IReadOnlyDictionary<string, string>,
                partial_payment = false
            };

            var orderRaw = await SendAsync(HttpMethod.Post, "v1/orders", orderBody, ct, "CreateOrder").ConfigureAwait(false);
            var order = JsonSerializer.Deserialize<RazorpayOrderResponse>(orderRaw);

            _logger.LogInformation("Razorpay order created: {OrderId} status={Status}", order?.Id, order?.Status);

            return new PaymentResponse
            {
                GatewayReference = order?.Id ?? string.Empty,
                Status = MapStatus(order?.Status ?? "created"),
                Amount = request.Amount,
                Currency = currency,
                ProcessedAt = DateTime.UtcNow,
                Message = $"Razorpay order created — direct customer to checkout with order_id={order?.Id}"
            };
        }

        // Default flow — capture a pre-authorised payment_id.
        var captureBody = new
        {
            amount = amountInPaise,
            currency
        };

        var raw = await SendAsync(
            HttpMethod.Post,
            $"v1/payments/{Uri.EscapeDataString(request.PaymentMethodToken)}/capture",
            captureBody, ct, "CapturePayment").ConfigureAwait(false);
        var payment = JsonSerializer.Deserialize<RazorpayPaymentResponse>(raw);

        _logger.LogInformation("Razorpay payment captured: {PaymentId} status={Status}", payment?.Id, payment?.Status);

        return new PaymentResponse
        {
            GatewayReference = payment?.Id ?? request.PaymentMethodToken,
            Status = MapStatus(payment?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            Message = payment?.Status
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInPaise = (long)(request.Amount * 100);
        var refundBody = new
        {
            amount = amountInPaise,
            speed = "normal",
            notes = new Dictionary<string, string> { ["reason"] = request.Reason ?? "Customer refund" }
        };

        var raw = await SendAsync(
            HttpMethod.Post,
            $"v1/payments/{Uri.EscapeDataString(request.GatewayReference)}/refund",
            refundBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<RazorpayRefundResponse>(raw);

        _logger.LogInformation("Razorpay refund created: {RefundId} for payment {PaymentId}",
            refund?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refund?.Id ?? string.Empty,
            Amount = request.Amount,
            Status = MapStatus(refund?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.RazorpayXAccountNumber))
            throw new ProviderConfigurationException(ProviderName,
                $"{nameof(RazorpayOptions.RazorpayXAccountNumber)} is required for RazorpayX payouts");

        var amountInPaise = (long)(request.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? _options.Currency
            : request.Currency.ToUpperInvariant();

        var payoutBody = new
        {
            account_number = _options.RazorpayXAccountNumber,
            amount = amountInPaise,
            currency,
            mode = "IMPS",
            purpose = "payout",
            fund_account_id = request.DestinationToken,
            queue_if_low_balance = true,
            reference_id = $"payout-{Guid.NewGuid():N}",
            narration = request.Description ?? "Bhengu payout"
        };

        var raw = await SendAsync(HttpMethod.Post, "v1/payouts", payoutBody, ct, "ProcessPayout").ConfigureAwait(false);
        var payout = JsonSerializer.Deserialize<RazorpayPayoutResponse>(raw);

        _logger.LogInformation("Razorpay payout created: {PayoutId} status={Status}", payout?.Id, payout?.Status);

        return new PayoutResponse
        {
            GatewayReference = payout?.Id ?? string.Empty,
            Status = MapStatus(payout?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Razorpay WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Razorpay webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<RazorpayWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Razorpay webhook event: {EventType}", webhookEvent.Event);

            var status = webhookEvent.Event?.ToLowerInvariant() switch
            {
                "payment.captured" or "payment.authorized" or "order.paid" => PaymentStatus.Completed,
                "payment.failed" => PaymentStatus.Failed,
                "refund.created" or "refund.processed" => PaymentStatus.Refunded,
                "payout.processed" => PaymentStatus.Completed,
                _ => (PaymentStatus?)null
            };

            var reference = webhookEvent.Payload?.Payment?.Entity?.Id
                ?? webhookEvent.Payload?.Refund?.Entity?.Id
                ?? webhookEvent.Payload?.Order?.Entity?.Id
                ?? webhookEvent.Payload?.Payout?.Entity?.Id;

            if (status is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = status.Value,
                EventType = webhookEvent.Event
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Razorpay webhook event");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Razorpay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Razorpay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "captured" or "paid" or "processed" or "success" or "succeeded" => PaymentStatus.Completed,
        "created" or "attempted" or "authorized" or "pending" or "queued" or "scheduled" or "initiated" => PaymentStatus.Pending,
        "failed" or "rejected" or "reversed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayOrderResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("receipt")] public string? Receipt { get; set; }
    }

    private sealed class RazorpayPaymentResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("method")] public string? Method { get; set; }
    }

    private sealed class RazorpayRefundResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("payment_id")] public string? PaymentId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class RazorpayPayoutResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("mode")] public string? Mode { get; set; }
    }

    private sealed class RazorpayWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("payload")] public RazorpayWebhookPayload? Payload { get; set; }
    }

    private sealed class RazorpayWebhookPayload
    {
        [JsonPropertyName("payment")] public RazorpayWebhookPaymentWrapper? Payment { get; set; }
        [JsonPropertyName("refund")] public RazorpayWebhookRefundWrapper? Refund { get; set; }
        [JsonPropertyName("order")] public RazorpayWebhookOrderWrapper? Order { get; set; }
        [JsonPropertyName("payout")] public RazorpayWebhookPayoutWrapper? Payout { get; set; }
    }

    private sealed class RazorpayWebhookPaymentWrapper
    {
        [JsonPropertyName("entity")] public RazorpayPaymentResponse? Entity { get; set; }
    }

    private sealed class RazorpayWebhookRefundWrapper
    {
        [JsonPropertyName("entity")] public RazorpayRefundResponse? Entity { get; set; }
    }

    private sealed class RazorpayWebhookOrderWrapper
    {
        [JsonPropertyName("entity")] public RazorpayOrderResponse? Entity { get; set; }
    }

    private sealed class RazorpayWebhookPayoutWrapper
    {
        [JsonPropertyName("entity")] public RazorpayPayoutResponse? Entity { get; set; }
    }
}
