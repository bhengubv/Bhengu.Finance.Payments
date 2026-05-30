// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Currency;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.BricsPay.Providers;

/// <summary>
/// BRICS Pay cross-border payments. Supports payments and payouts within BRICS nations
/// (South Africa, Brazil, Russia, India, China) with automatic currency conversion.
/// </summary>
public sealed class BricsPayPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly BricsPayOptions _options;
    private readonly ICurrencyExchangeService _exchangeService;
    private readonly ILogger<BricsPayPaymentProvider> _logger;
    private readonly string _baseUrl;

    public string ProviderName => ProviderNames.BricsPay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.CrossBorder;

    public BricsPayPaymentProvider(
        HttpClient httpClient,
        IOptions<BricsPayOptions> options,
        ICurrencyExchangeService exchangeService,
        ILogger<BricsPayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _exchangeService = exchangeService ?? throw new ArgumentNullException(nameof(exchangeService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.SecretKey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://sandbox.bricspay.org/api/v1")
            : (_options.BaseUrl ?? "https://api.bricspay.org/api/v1");
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceCurrency = ParseCurrency(request.Currency);
        var targetCurrency = request.Metadata?.TryGetValue("target_currency", out var tc) == true
            ? ParseCurrency(tc)
            : sourceCurrency;

        ConversionResult? conversion = null;
        if (sourceCurrency != targetCurrency)
        {
            conversion = await _exchangeService.LockRateAsync(request.Amount, sourceCurrency, targetCurrency, ct: ct).ConfigureAwait(false);
            _logger.LogInformation("Currency conversion {Amount} {From} -> {Final} {To} @ {Rate}",
                request.Amount, sourceCurrency, conversion.FinalAmount, targetCurrency, conversion.ExchangeRate);
        }

        var transactionId = GenerateTransactionId();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var requestBody = new
        {
            merchant_id = _options.MerchantId,
            transaction_id = transactionId,
            payment_method_token = request.PaymentMethodToken,
            amount = conversion?.FinalAmount ?? request.Amount,
            currency = targetCurrency.ToString(),
            source_currency = sourceCurrency.ToString(),
            source_amount = request.Amount,
            exchange_rate = conversion?.ExchangeRate ?? 1m,
            quote_id = conversion?.QuoteId,
            description = request.Description,
            metadata = request.Metadata,
            timestamp
        };

        var response = await SendSignedRequestAsync($"{_baseUrl}/payments", requestBody, timestamp, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("BRICS Pay payment failed: {StatusCode} {Body}", response.StatusCode, body);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body);
        return new PaymentResponse
        {
            GatewayReference = result?.PaymentId ?? transactionId,
            Status = MapStatus(result?.Status ?? "pending"),
            Amount = conversion?.FinalAmount ?? request.Amount,
            Currency = targetCurrency.ToString(),
            ProcessedAt = DateTime.UtcNow,
            Message = result?.Message
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestBody = new
        {
            merchant_id = _options.MerchantId,
            original_payment_id = request.GatewayReference,
            refund_amount = request.Amount,
            reason = request.Reason,
            timestamp
        };

        var response = await SendSignedRequestAsync($"{_baseUrl}/refunds", requestBody, timestamp, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("BRICS Pay refund failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new BhenguPaymentException(
                ProviderName,
                $"BRICS Pay refund failed: HTTP {(int)response.StatusCode}",
                providerErrorCode: ((int)response.StatusCode).ToString(),
                providerErrorMessage: body);
        }

        var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body);
        return new RefundResponse
        {
            GatewayReference = result?.PaymentId ?? $"REFUND_{Guid.NewGuid():N}",
            Amount = request.Amount,
            Status = MapStatus(result?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = result?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transactionId = GenerateTransactionId();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var requestBody = new
        {
            merchant_id = _options.MerchantId,
            transaction_id = transactionId,
            destination_token = request.DestinationToken,
            amount = request.Amount,
            currency = request.Currency,
            description = request.Description,
            timestamp
        };

        var response = await SendSignedRequestAsync($"{_baseUrl}/payouts", requestBody, timestamp, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("BRICS Pay payout failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new BhenguPaymentException(
                ProviderName,
                $"BRICS Pay payout failed: HTTP {(int)response.StatusCode}",
                providerErrorCode: ((int)response.StatusCode).ToString(),
                providerErrorMessage: body);
        }

        var result = JsonSerializer.Deserialize<BricsPayApiResponse>(body);
        return new PayoutResponse
        {
            GatewayReference = result?.PaymentId ?? transactionId,
            Status = MapStatus(result?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("BRICS Pay WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computed = Convert.ToBase64String(computedHash);

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(computed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BRICS Pay webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<BricsPayWebhookPayload>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhookEvent.PaymentId,
                Status = MapStatus(webhookEvent.Status),
                EventType = webhookEvent.EventType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse BRICS Pay webhook payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<HttpResponseMessage> SendSignedRequestAsync(string url, object body, long timestamp, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        var signature = GenerateSignature(json, timestamp);

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("X-Merchant-Id", _options.MerchantId);
        req.Headers.Add("X-Signature", signature);
        req.Headers.Add("X-Timestamp", timestamp.ToString());

        try
        {
            return await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to BRICS Pay failed", ex);
        }
    }

    private string GenerateSignature(string serializedBody, long timestamp)
    {
        var payload = serializedBody + timestamp + _options.SecretKey;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private static string GenerateTransactionId() =>
        $"BRICS_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..32];

    private static PaymentStatus MapStatus(string raw) => raw.ToLowerInvariant() switch
    {
        "success" or "completed" or "settled" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" or "error" => PaymentStatus.Failed,
        "cancelled" or "voided" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private static BricsCurrency ParseCurrency(string currency) =>
        Enum.TryParse<BricsCurrency>(currency, ignoreCase: true, out var result) ? result : BricsCurrency.ZAR;

    private sealed record BricsPayApiResponse
    {
        public string? PaymentId { get; init; }
        public string? Status { get; init; }
        public string? Message { get; init; }
    }

    private sealed record BricsPayWebhookPayload
    {
        public string EventType { get; init; } = string.Empty;
        public string PaymentId { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public decimal Amount { get; init; }
        public string Currency { get; init; } = string.Empty;
    }
}
