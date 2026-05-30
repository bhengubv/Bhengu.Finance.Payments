// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Kashier.Providers;

/// <summary>
/// Kashier (Egypt + UAE + KSA) payment gateway provider. Wraps the Kashier REST API
/// and the hosted-payment-page hash protocol. Implements <see cref="IPayoutProvider"/>
/// because Kashier exposes a /payouts endpoint for marketplace disbursements.
/// </summary>
public sealed class KashierPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private const string DefaultBaseUrl = "https://api.kashier.io/";

    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;
    private readonly ILogger<KashierPaymentProvider> _logger;

    public string ProviderName => "kashier";

    public KashierPaymentProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? DefaultBaseUrl);

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(_options.ApiKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderId = request.Metadata?.GetValueOrDefault("orderId") ?? $"kashier-{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
        var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();

        var requestBody = new
        {
            amount,
            currency,
            shopperReference = request.Metadata?.GetValueOrDefault("shopperReference"),
            cardData = request.PaymentMethodToken,
            description = request.Description
        };

        var body = await SendAsync(HttpMethod.Post, $"orders/{orderId}/payments", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var kashierResponse = JsonSerializer.Deserialize<KashierPaymentResponse>(body);

        _logger.LogInformation("Kashier charge created: order={OrderId} tx={Tx} status={Status}",
            orderId, kashierResponse?.Response?.TransactionId, kashierResponse?.Response?.Status);

        var txId = kashierResponse?.Response?.TransactionId ?? orderId;

        return new PaymentResponse
        {
            GatewayReference = txId,
            Status = MapStatus(kashierResponse?.Response?.Status),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            Message = kashierResponse?.Response?.Status
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Kashier identifies refund targets by transactionId; orderId is optional in the body
        // and only needed when the caller is operating in reconcile-by-order mode. We send the
        // gateway reference as both fields so either lookup mode resolves the same target.
        var requestBody = new
        {
            merchantId = _options.MerchantId,
            orderId = request.GatewayReference,
            transactionId = request.GatewayReference,
            amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            reason = request.Reason
        };

        var body = await SendAsync(HttpMethod.Post, "payments/refund", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<KashierPaymentResponse>(body);

        _logger.LogInformation("Kashier refund created: tx={Tx} status={Status}",
            request.GatewayReference, refundResponse?.Response?.Status);

        var mapped = MapStatus(refundResponse?.Response?.Status);
        return new RefundResponse
        {
            GatewayReference = refundResponse?.Response?.TransactionId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = mapped is PaymentStatus.Completed or PaymentStatus.Refunded ? PaymentStatus.Refunded : mapped,
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Response?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = new
        {
            merchantId = _options.MerchantId,
            destination = request.DestinationToken,
            amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            description = request.Description
        };

        var body = await SendAsync(HttpMethod.Post, "payouts", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var payoutResponse = JsonSerializer.Deserialize<KashierPaymentResponse>(body);

        _logger.LogInformation("Kashier payout created: id={Id} status={Status}",
            payoutResponse?.Response?.TransactionId, payoutResponse?.Response?.Status);

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.Response?.TransactionId ?? string.Empty,
            Amount = request.Amount,
            Currency = request.Currency,
            Status = MapStatus(payoutResponse?.Response?.Status),
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.SecretKey : _options.WebhookSecret;
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Kashier webhook secret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kashier webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhook = JsonSerializer.Deserialize<KashierWebhookEvent>(payload);
            if (webhook is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Kashier webhook: event={Event} status={Status}",
                webhook.Event, webhook.Data?.Status);

            var status = webhook.Event?.ToUpperInvariant() switch
            {
                "PAY" or "CAPTURE" => webhook.Data?.Status?.ToUpperInvariant() == "SUCCESS" ? PaymentStatus.Completed : PaymentStatus.Pending,
                "REFUND" => PaymentStatus.Refunded,
                "VOID" => PaymentStatus.Cancelled,
                "FAILED" => PaymentStatus.Failed,
                _ => MapStatus(webhook.Data?.Status)
            };

            if (status == PaymentStatus.Pending && string.IsNullOrEmpty(webhook.Event))
                return Task.FromResult<WebhookEvent?>(null);

            var gatewayRef = webhook.Data?.TransactionId ?? webhook.Data?.OrderId;
            if (string.IsNullOrEmpty(gatewayRef))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = gatewayRef,
                Status = status,
                EventType = webhook.Event
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Kashier webhook");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    /// <summary>
    /// Build a hosted-payment-page URL for the redirect flow. Pure helper — exposed so consumers
    /// who prefer the hosted page over the server-to-server charge can build a signed redirect.
    /// </summary>
    public string BuildHostedPaymentUrl(string orderId, decimal amount, string? currency = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);
        var amt = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var ccy = string.IsNullOrWhiteSpace(currency) ? _options.Currency : currency.ToUpperInvariant();
        var mode = _options.UseSandbox ? "test" : (string.IsNullOrWhiteSpace(_options.Mode) ? "live" : _options.Mode);
        var hash = ComputeHostedPageHash(_options.MerchantId, orderId, amt, ccy, _options.SecretKey);

        var sb = new StringBuilder();
        sb.Append(_httpClient.BaseAddress?.ToString().TrimEnd('/') ?? DefaultBaseUrl);
        sb.Append("/pay?merchantId=").Append(Uri.EscapeDataString(_options.MerchantId))
          .Append("&orderId=").Append(Uri.EscapeDataString(orderId))
          .Append("&amount=").Append(Uri.EscapeDataString(amt))
          .Append("&currency=").Append(Uri.EscapeDataString(ccy))
          .Append("&hash=").Append(hash)
          .Append("&mode=").Append(Uri.EscapeDataString(mode));
        if (!string.IsNullOrWhiteSpace(_options.RedirectUrl))
            sb.Append("&merchantRedirect=").Append(Uri.EscapeDataString(_options.RedirectUrl));
        if (!string.IsNullOrWhiteSpace(_options.ServerWebhookUrl))
            sb.Append("&serverWebhook=").Append(Uri.EscapeDataString(_options.ServerWebhookUrl));
        return sb.ToString();
    }

    internal static string ComputeHostedPageHash(
        string merchantId, string orderId, string amount, string currency, string secretKey)
    {
        // Kashier hosted-page hash: SHA-256 hex of "/?payment=mid.orderId.amount.currency" + SecretKey.
        var path = $"/?payment={merchantId}.{orderId}.{amount}.{currency}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey ?? string.Empty));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Kashier failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Kashier {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "SUCCESS" or "CAPTURED" or "PAID" or "COMPLETED" or "APPROVED" => PaymentStatus.Completed,
        "PENDING" or "PROCESSING" or "INPROGRESS" or "INITIATED" => PaymentStatus.Pending,
        "FAILED" or "DECLINED" or "REJECTED" => PaymentStatus.Failed,
        "CANCELED" or "CANCELLED" or "VOIDED" => PaymentStatus.Cancelled,
        "REFUNDED" or "PARTIAL_REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Kashier API response shapes (internal) ===

    private sealed class KashierPaymentResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("messages")] public KashierMessages? Messages { get; set; }
        [JsonPropertyName("response")] public KashierPaymentData? Response { get; set; }
    }

    private sealed class KashierMessages
    {
        [JsonPropertyName("en")] public string? En { get; set; }
    }

    private sealed class KashierPaymentData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class KashierWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public KashierPaymentData? Data { get; set; }
    }
}

