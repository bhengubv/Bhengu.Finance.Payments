// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Interswitch.Providers;

/// <summary>
/// Interswitch (Nigeria/Africa) payment gateway provider. Wraps the Quickteller and Disbursement
/// REST APIs over Interswitch's OAuth2 Passport endpoint. Supports payments, refunds, and disbursements.
/// </summary>
public sealed class InterswitchPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private const string ProductionBaseUrl = "https://passport.interswitchng.com";
    private const string SandboxBaseUrl = "https://qa.interswitchng.com";

    private readonly HttpClient _httpClient;
    private readonly InterswitchOptions _options;
    private readonly ILogger<InterswitchPaymentProvider> _logger;

    private string? _cachedAccessToken;
    private DateTime _accessTokenExpiresAtUtc = DateTime.MinValue;

    public string ProviderName => "interswitch";

    public InterswitchPaymentProvider(
        HttpClient httpClient,
        IOptions<InterswitchOptions> options,
        ILogger<InterswitchPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var baseUrl = _options.UseSandbox
                ? _options.SandboxUrl ?? SandboxBaseUrl
                : _options.BaseUrl ?? ProductionBaseUrl;
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInKobo = (long)(request.Amount * 100);
        var requestRef = request.Metadata?.GetValueOrDefault("requestReference") ?? $"isw-{Guid.NewGuid():N}";
        var customerEmail = request.Metadata?.GetValueOrDefault("customerEmail") ?? "noreply@bhengu.example";
        var customerId = request.Metadata?.GetValueOrDefault("customerId") ?? "anonymous";
        var customerMobile = request.Metadata?.GetValueOrDefault("mobileNo") ?? "";

        var requestBody = new
        {
            customer = new { id = customerId, mobileNo = customerMobile },
            paymentCode = _options.ProductId,
            customerEmail,
            amount = amountInKobo,
            currency = request.Currency.ToUpperInvariant(),
            transferCode = request.PaymentMethodToken,
            requestReference = requestRef
        };

        var body = await SendAsync(HttpMethod.Post, "api/v2/quickteller/payments/advices",
            requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<InterswitchAdviceResponse>(body);

        _logger.LogInformation("Interswitch advice created: {Ref} status={Status}",
            resp?.TransactionRef ?? requestRef, resp?.ResponseCode);

        return new PaymentResponse
        {
            GatewayReference = resp?.TransactionRef ?? requestRef,
            Status = MapResponseCode(resp?.ResponseCode),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.ResponseDescription
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInKobo = (long)(request.Amount * 100);
        var requestBody = new
        {
            amount = amountInKobo,
            reason = request.Reason
        };

        var path = $"api/v2/quickteller/transactions/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<InterswitchRefundResponse>(body);

        _logger.LogInformation("Interswitch refund created: {RefundRef} for txn {TxnRef}",
            resp?.RefundReference, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = resp?.RefundReference ?? string.Empty,
            Amount = request.Amount,
            Status = MapResponseCode(resp?.ResponseCode),
            ProcessedAt = DateTime.UtcNow,
            Message = resp?.ResponseDescription
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInKobo = (long)(request.Amount * 100);
        // DestinationToken format: "<bankCode>:<accountNumber>" or just "<accountNumber>" (with bank in metadata downstream)
        string bankCode = string.Empty;
        string accountNumber = request.DestinationToken;
        var sep = request.DestinationToken.IndexOf(':');
        if (sep > 0)
        {
            bankCode = request.DestinationToken[..sep];
            accountNumber = request.DestinationToken[(sep + 1)..];
        }

        var requestBody = new
        {
            amount = amountInKobo,
            beneficiaryAccountNumber = accountNumber,
            beneficiaryBankCode = bankCode,
            narration = request.Description,
            transactionRef = $"disb-{Guid.NewGuid():N}",
            currencyCode = request.Currency.ToUpperInvariant()
        };

        var body = await SendAsync(HttpMethod.Post, "api/v2/disbursements/transactions",
            requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<InterswitchDisbursementResponse>(body);

        _logger.LogInformation("Interswitch disbursement created: {Ref} status={Status}",
            resp?.TransactionRef, resp?.ResponseCode);

        return new PayoutResponse
        {
            GatewayReference = resp?.TransactionRef ?? string.Empty,
            Status = MapResponseCode(resp?.ResponseCode),
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
            _logger.LogWarning("Interswitch WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interswitch webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var evt = JsonSerializer.Deserialize<InterswitchWebhookEvent>(payload);
            if (evt is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Interswitch webhook event: {EventType}", evt.EventType);

            var status = evt.EventType?.ToLowerInvariant() switch
            {
                "payment.successful" or "transaction.successful" or "disbursement.successful"
                    => PaymentStatus.Completed,
                "payment.failed" or "transaction.failed" or "disbursement.failed"
                    => PaymentStatus.Failed,
                "refund.successful" or "refund.processed"
                    => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(evt.Data?.TransactionRef))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.Data.TransactionRef,
                Status = status.Value,
                EventType = evt.EventType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Interswitch webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // Interswitch security headers
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var nonce = Guid.NewGuid().ToString("N");
        var resourceUrl = path.StartsWith('/') ? path : "/" + path;
        var signature = ComputeRequestSignature(method.Method, resourceUrl, timestampMs, nonce);

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Signature", signature);
        req.Headers.TryAddWithoutValidation("SignatureMethod", "SHA-512");
        req.Headers.TryAddWithoutValidation("Timestamp", timestampMs);
        req.Headers.TryAddWithoutValidation("Nonce", nonce);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Interswitch failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Interswitch {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTime.UtcNow < _accessTokenExpiresAtUtc.AddSeconds(-30))
            return _cachedAccessToken;

        using var req = new HttpRequestMessage(HttpMethod.Post, "passport/oauth/token");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        req.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "client_credentials"),
            new KeyValuePair<string, string>("scope", "profile")
        });

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "Interswitch token endpoint unreachable", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Interswitch OAuth2 token failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new ProviderUnavailableException(ProviderName, $"OAuth2 token HTTP {(int)response.StatusCode}: {body}");
        }

        var token = JsonSerializer.Deserialize<InterswitchTokenResponse>(body);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new ProviderUnavailableException(ProviderName, "Interswitch OAuth2 token response missing access_token");

        _cachedAccessToken = token.AccessToken;
        _accessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600);
        return _cachedAccessToken;
    }

    private string ComputeRequestSignature(string method, string resource, string timestampMs, string nonce)
    {
        // Interswitch documented format: SHA-512 hex of clientId+method+resource+timestamp+nonce+secretKey
        var raw = _options.ClientId + method + resource + timestampMs + nonce + _options.ClientSecret;
        using var sha = SHA512.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static PaymentStatus MapResponseCode(string? code) => code switch
    {
        "00" or "0" or "SUCCESS" or "Success" or "successful" => PaymentStatus.Completed,
        "09" or "PENDING" or "pending" or "processing" => PaymentStatus.Pending,
        "10" or "REFUNDED" or "refunded" => PaymentStatus.Refunded,
        null or "" => PaymentStatus.Pending,
        _ => PaymentStatus.Failed
    };

    // === Interswitch API response shapes (internal) ===

    private sealed class InterswitchTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class InterswitchAdviceResponse
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class InterswitchRefundResponse
    {
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class InterswitchDisbursementResponse
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("responseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class InterswitchWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public InterswitchWebhookData? Data { get; set; }
    }

    private sealed class InterswitchWebhookData
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
