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
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.JamboPay.Providers;

/// <summary>
/// JamboPay (Kenya) payment gateway provider. Wraps JamboPay v1.
/// Auth is dual: static x-api-key header PLUS short-lived Bearer token from
/// /oauth/token (client_credentials). Supports collections, refunds and payouts;
/// webhook signature is HMAC-SHA256 (hex) over the raw body keyed by WebhookSecret.
/// </summary>
public sealed class JamboPayPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly JamboPayOptions _options;
    private readonly ILogger<JamboPayPaymentProvider> _logger;
    private string? _cachedToken;
    private DateTime _tokenExpiresAtUtc;

    public string ProviderName => ProviderNames.JamboPay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.BankTransfer;

    public JamboPayPaymentProvider(
        HttpClient httpClient,
        IOptions<JamboPayOptions> options,
        ILogger<JamboPayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(JamboPayOptions.MerchantCode)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.BaseUrl ?? "https://api.jambopay.com/v1/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new
        {
            merchant_code = _options.MerchantCode,
            transaction_ref = request.PaymentMethodToken,
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            customer = new
            {
                email = request.Metadata?.GetValueOrDefault("email") ?? "",
                msisdn = request.Metadata?.GetValueOrDefault("msisdn") ?? "",
                name = request.Metadata?.GetValueOrDefault("name") ?? ""
            },
            payment_method = request.Metadata?.GetValueOrDefault("payment_method") ?? "CARD",
            callback_url = _options.CallbackUrl
        };

        var responseBody = await SendAsync(HttpMethod.Post, "payments/initiate", body, ct, "ProcessPayment").ConfigureAwait(false);
        var payment = JsonSerializer.Deserialize<JamboPayInitiateResponse>(responseBody);

        _logger.LogInformation("JamboPay payment initiated: ref={Ref} status={Status} url={Url}",
            request.PaymentMethodToken, payment?.Status, payment?.CheckoutUrl);

        return new PaymentResponse
        {
            GatewayReference = payment?.TransactionRef ?? request.PaymentMethodToken,
            Status = MapStatus(payment?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = payment?.CheckoutUrl,
            Message = payment?.Message
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new
        {
            transaction_ref = request.GatewayReference,
            amount = request.Amount,
            reason = request.Reason
        };

        var responseBody = await SendAsync(HttpMethod.Post, "payments/refund", body, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<JamboPayRefundResponse>(responseBody);

        _logger.LogInformation("JamboPay refund: id={Id} status={Status} for {Ref}",
            refund?.RefundId, refund?.Status, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refund?.RefundId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refund?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken layouts:
        //   "msisdn:254700000000"          → mobile-money payout
        //   "bank:KCBLKENX:1234567890"     → bank payout (bankCode + account)
        var token = request.DestinationToken;
        object beneficiary;
        if (token.StartsWith("msisdn:", StringComparison.OrdinalIgnoreCase))
            beneficiary = new { msisdn = token["msisdn:".Length..] };
        else if (token.StartsWith("bank:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = token["bank:".Length..];
            var colon = rest.IndexOf(':');
            if (colon <= 0)
                throw new BhenguPaymentException(ProviderName,
                    "JamboPay PayoutRequest.DestinationToken bank form must be 'bank:<bankCode>:<accountNumber>'");
            beneficiary = new { bank_code = rest[..colon], account_number = rest[(colon + 1)..] };
        }
        else
            throw new BhenguPaymentException(ProviderName,
                "JamboPay PayoutRequest.DestinationToken must be 'msisdn:<phone>' or 'bank:<code>:<account>'");

        var body = new
        {
            merchant_code = _options.MerchantCode,
            beneficiary,
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            narration = request.Description
        };

        var responseBody = await SendAsync(HttpMethod.Post, "payouts/initiate", body, ct, "ProcessPayout").ConfigureAwait(false);
        var payout = JsonSerializer.Deserialize<JamboPayPayoutResponse>(responseBody);

        _logger.LogInformation("JamboPay payout initiated: id={Id} status={Status}", payout?.PayoutId, payout?.Status);

        return new PayoutResponse
        {
            GatewayReference = payout?.PayoutId ?? string.Empty,
            Status = MapStatus(payout?.Status ?? "pending"),
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
            _logger.LogWarning("JamboPay WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(hex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JamboPay webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var evt = JsonSerializer.Deserialize<JamboPayWebhookEvent>(payload);
            if (evt is null || string.IsNullOrEmpty(evt.TransactionRef))
                return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed JamboPay webhook event: {EventType}", evt.EventType);

            var status = evt.EventType?.ToLowerInvariant() switch
            {
                "payment.completed" or "payment.success" => PaymentStatus.Completed,
                "payment.failed" => PaymentStatus.Failed,
                "payment.cancelled" => PaymentStatus.Cancelled,
                "refund.completed" => PaymentStatus.Refunded,
                "payout.completed" => PaymentStatus.Completed,
                "payout.failed" => PaymentStatus.Failed,
                _ => (PaymentStatus?)null
            };

            if (status is null) return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.TransactionRef,
                Status = status.Value,
                EventType = evt.EventType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse JamboPay webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiresAtUtc)
            return _cachedToken!;

        using var req = new HttpRequestMessage(HttpMethod.Post, "oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret
            })
        };
        req.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to JamboPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        var auth = JsonSerializer.Deserialize<JamboPayAuthResponse>(responseBody);
        if (string.IsNullOrWhiteSpace(auth?.AccessToken))
            throw new ProviderUnavailableException(ProviderName, "JamboPay /oauth/token returned an empty access_token");

        _cachedToken = auth!.AccessToken;
        var ttlSeconds = auth.ExpiresIn > 30 ? auth.ExpiresIn - 30 : 60;
        _tokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(ttlSeconds);
        return _cachedToken!;
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var token = await EnsureTokenAsync(ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(body);

        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to JamboPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("JamboPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "successful" or "succeeded" or "success" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private sealed class JamboPayAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class JamboPayInitiateResponse
    {
        [JsonPropertyName("transaction_ref")] public string? TransactionRef { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("checkout_url")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class JamboPayRefundResponse
    {
        [JsonPropertyName("refund_id")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class JamboPayPayoutResponse
    {
        [JsonPropertyName("payout_id")] public string? PayoutId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class JamboPayWebhookEvent
    {
        [JsonPropertyName("event")] public string? EventType { get; set; }
        [JsonPropertyName("transaction_ref")] public string? TransactionRef { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
