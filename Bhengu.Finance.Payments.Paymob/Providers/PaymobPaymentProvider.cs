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
using Bhengu.Finance.Payments.Paymob.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paymob.Providers;

/// <summary>
/// Paymob (Egypt + GCC + Pakistan) payment gateway provider. Wraps the Paymob Accept REST API
/// and the Paymob Disbursement API. ProcessPayment performs the full 4-step Accept handshake:
/// authenticate → create order → create payment key → return iframe URL/token.
/// </summary>
public sealed class PaymobPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private const string DefaultBaseUrl = "https://accept.paymob.com/";

    private readonly HttpClient _httpClient;
    private readonly PaymobOptions _options;
    private readonly ILogger<PaymobPaymentProvider> _logger;

    public string ProviderName => "paymob";

    public PaymobPaymentProvider(
        HttpClient httpClient,
        IOptions<PaymobOptions> options,
        ILogger<PaymobPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaymobOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? DefaultBaseUrl);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var integrationId = request.Metadata?.GetValueOrDefault("integration_id") is { Length: > 0 } iidStr
                && int.TryParse(iidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iid)
            ? iid
            : _options.IntegrationId;
        var iframeId = request.Metadata?.GetValueOrDefault("iframe_id") is { Length: > 0 } ifStr
                && int.TryParse(ifStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ifv)
            ? ifv
            : _options.IframeId;

        if (integrationId <= 0)
            throw new PaymentDeclinedException(ProviderName, "missing_integration_id",
                "Paymob requires an 'integration_id' in PaymentRequest.Metadata or PaymobOptions.IntegrationId.");

        var amountCents = (long)(request.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();

        // 1. Authenticate
        var authBody = new { api_key = _options.ApiKey };
        var authJson = await SendAsync(HttpMethod.Post, "api/auth/tokens", authBody, ct, "Authenticate").ConfigureAwait(false);
        var authResponse = JsonSerializer.Deserialize<PaymobAuthResponse>(authJson);
        var authToken = authResponse?.Token;
        if (string.IsNullOrEmpty(authToken))
            throw new ProviderUnavailableException(ProviderName, "Paymob auth returned no token");

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
        var orderJson = await SendAsync(HttpMethod.Post, "api/ecommerce/orders", orderBody, ct, "CreateOrder").ConfigureAwait(false);
        var orderResponse = JsonSerializer.Deserialize<PaymobOrderResponse>(orderJson);
        var orderId = orderResponse?.Id;
        if (orderId is null || orderId == 0)
            throw new ProviderUnavailableException(ProviderName, "Paymob order creation returned no id");

        // 3. Create payment key
        var billing = new
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
        var keyBody = new
        {
            auth_token = authToken,
            amount_cents = amountCents,
            expiration = 3600,
            order_id = orderId,
            billing_data = billing,
            currency,
            integration_id = integrationId,
            lock_order_when_paid = true
        };
        var keyJson = await SendAsync(HttpMethod.Post, "api/acceptance/payment_keys", keyBody, ct, "CreatePaymentKey").ConfigureAwait(false);
        var keyResponse = JsonSerializer.Deserialize<PaymobPaymentKeyResponse>(keyJson);
        var paymentKey = keyResponse?.Token;
        if (string.IsNullOrEmpty(paymentKey))
            throw new ProviderUnavailableException(ProviderName, "Paymob payment_keys returned no token");

        _logger.LogInformation("Paymob 4-step flow complete: order={OrderId} integration={IntegrationId}",
            orderId, integrationId);

        var iframeUrl = iframeId > 0
            ? $"https://accept.paymob.com/api/acceptance/iframes/{iframeId}?payment_token={paymentKey}"
            : paymentKey;

        return new PaymentResponse
        {
            GatewayReference = orderId.Value.ToString(CultureInfo.InvariantCulture),
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            Message = iframeUrl
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authToken = await GetAuthTokenAsync(ct).ConfigureAwait(false);
        var amountCents = (long)(request.Amount * 100);
        var refundBody = new
        {
            auth_token = authToken,
            transaction_id = request.GatewayReference,
            amount_cents = amountCents
        };

        var body = await SendAsync(HttpMethod.Post, "api/acceptance/void_refund/refund", refundBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<PaymobTransactionResponse>(body);

        _logger.LogInformation("Paymob refund completed for transaction {Transaction} success={Success}",
            request.GatewayReference, refund?.Success);

        return new RefundResponse
        {
            GatewayReference = refund?.Id?.ToString(CultureInfo.InvariantCulture) ?? request.GatewayReference,
            Amount = request.Amount,
            Status = refund?.Success == true ? PaymentStatus.Refunded : PaymentStatus.Pending,
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Success.ToString()
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var authToken = await GetAuthTokenAsync(ct).ConfigureAwait(false);
        var amountCents = (long)(request.Amount * 100);
        var disbursementBody = new
        {
            auth_token = authToken,
            amount_cents = amountCents,
            currency = request.Currency.ToUpperInvariant(),
            destination = request.DestinationToken,
            description = request.Description
        };

        var body = await SendAsync(HttpMethod.Post, "api/disbursements/transactions", disbursementBody, ct, "ProcessPayout").ConfigureAwait(false);
        var disbursement = JsonSerializer.Deserialize<PaymobTransactionResponse>(body);

        _logger.LogInformation("Paymob disbursement created id={Id} success={Success}",
            disbursement?.Id, disbursement?.Success);

        return new PayoutResponse
        {
            GatewayReference = disbursement?.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = disbursement?.Success == true ? PaymentStatus.Completed : PaymentStatus.Pending,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.HmacSecret))
        {
            _logger.LogWarning("Paymob HmacSecret not configured — webhook signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.HmacSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paymob webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var callback = JsonSerializer.Deserialize<PaymobWebhookCallback>(payload);
            if (callback?.Obj is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Paymob webhook: type={Type} success={Success} id={Id}",
                callback.Type, callback.Obj.Success, callback.Obj.Id);

            // Paymob fires TRANSACTION callbacks with success=true/false; refunds carry is_refunded=true.
            // We do not surface auth-only events.
            PaymentStatus? status = null;
            if (callback.Obj.IsRefunded == true)
                status = PaymentStatus.Refunded;
            else if (callback.Obj.IsVoided == true)
                status = PaymentStatus.Cancelled;
            else if (callback.Obj.Pending == true)
                status = PaymentStatus.Pending;
            else if (callback.Obj.Success == true)
                status = PaymentStatus.Completed;
            else if (callback.Obj.Success == false)
                status = PaymentStatus.Failed;

            if (status is null || callback.Obj.Id is null)
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = callback.Obj.Id.Value.ToString(CultureInfo.InvariantCulture),
                Status = status.Value,
                EventType = callback.Type ?? "TRANSACTION"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Paymob webhook callback");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> GetAuthTokenAsync(CancellationToken ct)
    {
        var authBody = new { api_key = _options.ApiKey };
        var authJson = await SendAsync(HttpMethod.Post, "api/auth/tokens", authBody, ct, "Authenticate").ConfigureAwait(false);
        var authResponse = JsonSerializer.Deserialize<PaymobAuthResponse>(authJson);
        if (string.IsNullOrEmpty(authResponse?.Token))
            throw new ProviderUnavailableException(ProviderName, "Paymob auth returned no token");
        return authResponse.Token;
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Paymob failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Paymob {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    // === Paymob API response shapes (internal) ===

    private sealed class PaymobAuthResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
    }

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
        [JsonPropertyName("is_voided")] public bool? IsVoided { get; set; }
    }

    private sealed class PaymobWebhookCallback
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("obj")] public PaymobTransactionResponse? Obj { get; set; }
    }
}
