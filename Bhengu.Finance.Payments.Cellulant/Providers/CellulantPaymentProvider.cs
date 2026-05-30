// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Cellulant.Providers;

/// <summary>
/// Cellulant (Tingg / Mula) pan-African aggregator. Wraps the Tingg Express Checkout v3 API for
/// collections and refunds, and the Mula disbursement endpoint for payouts. OAuth2 access tokens
/// are minted on demand using the configured client credentials. Webhooks are HMAC-SHA256 signed
/// via the <c>x-tingg-signature</c> header.
/// </summary>
public sealed class CellulantPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly CellulantOptions _options;
    private readonly ILogger<CellulantPaymentProvider> _logger;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public string ProviderName => ProviderNames.Cellulant;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.CrossBorder;

    public CellulantPaymentProvider(
        HttpClient httpClient,
        IOptions<CellulantOptions> options,
        ILogger<CellulantPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ServiceCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ServiceCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(CellulantOptions.ClientSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://online.uat.tingg.africa/"
                : "https://online.tingg.africa/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = request.Metadata?.GetValueOrDefault("email") ?? "noreply@example.com";
        var name = request.Metadata?.GetValueOrDefault("name") ?? "Customer";
        var msisdn = request.PaymentMethodToken;

        var merchantTransactionId = string.IsNullOrEmpty(_options.MerchantTransactionId)
            ? $"tingg-{Guid.NewGuid():N}"
            : $"{_options.MerchantTransactionId}-{Guid.NewGuid():N}";

        var requestBody = new
        {
            msisdn,
            accountNumber = msisdn,
            payerEmail = email,
            payerClientCode = msisdn,
            payerClientName = name,
            payerAuthEmail = email,
            requestAmount = request.Amount,
            currencyCode = request.Currency.ToUpperInvariant(),
            serviceCode = _options.ServiceCode,
            merchantTransactionId,
            requestDescription = request.Description,
            dueDate = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd HH:mm:ss"),
            languageCode = "en",
            successRedirectUrl = _options.CallbackUrl,
            failRedirectUrl = _options.CallbackUrl,
            paymentWebhookUrl = _options.CallbackUrl,
            countryCode = _options.CountryCode
        };

        var body = await SendAuthorisedAsync(HttpMethod.Post, "checkout/v3/express", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<CellulantCheckoutResponse>(body);

        _logger.LogInformation("Cellulant checkout created: {Id} status={Status}",
            response?.CheckoutRequestId, response?.Status);

        return new PaymentResponse
        {
            GatewayReference = response?.CheckoutRequestId ?? merchantTransactionId,
            Status = MapStatus(response?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = response?.RedirectUrl,
            Message = response?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = new
        {
            sourceServiceCode = _options.ServiceCode,
            destinationMSISDN = request.DestinationToken,
            currencyCode = request.Currency.ToUpperInvariant(),
            amount = request.Amount,
            narration = request.Description,
            countryCode = _options.CountryCode,
            externalReference = $"mula-{Guid.NewGuid():N}"
        };

        var body = await SendAuthorisedAsync(HttpMethod.Post, "disbursement/v1/initiate", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<CellulantPayoutResponse>(body);

        _logger.LogInformation("Cellulant Mula disbursement initiated: {Reference} status={Status}",
            response?.TransactionReference, response?.Status);

        return new PayoutResponse
        {
            GatewayReference = response?.TransactionReference ?? string.Empty,
            Status = MapStatus(response?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = new
        {
            transactionId = request.GatewayReference,
            amount = request.Amount,
            reason = request.Reason
        };

        var body = await SendAuthorisedAsync(HttpMethod.Post, "refunds", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<CellulantRefundResponse>(body);

        _logger.LogInformation("Cellulant refund processed: {Reference} for transaction {TransactionId}",
            response?.RefundReference, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = response?.RefundReference ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(response?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = response?.Status
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Cellulant WebhookSecret not configured — signature verification cannot succeed.");
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
            _logger.LogError(ex, "Cellulant webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<CellulantWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Cellulant webhook event: {EventType}", webhookEvent.EventType);

            var status = webhookEvent.EventType?.ToLowerInvariant() switch
            {
                "payment.success" or "checkout.success" => PaymentStatus.Completed,
                "payment.failed" or "checkout.failed" => PaymentStatus.Failed,
                "refund.success" or "refund.processed" => PaymentStatus.Refunded,
                "disbursement.success" => PaymentStatus.Completed,
                "disbursement.failed" => PaymentStatus.Failed,
                _ => (PaymentStatus?)null
            };

            var reference = webhookEvent.Data?.CheckoutRequestId ?? webhookEvent.Data?.TransactionId;
            if (status is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = status.Value,
                EventType = webhookEvent.EventType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Cellulant webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - TimeSpan.FromMinutes(1))
            return _accessToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - TimeSpan.FromMinutes(1))
                return _accessToken;

            var requestBody = new
            {
                grant_type = "client_credentials",
                client_id = _options.ClientId,
                client_secret = _options.ClientSecret
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/oauth/token/request")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "HTTP request to Cellulant OAuth failed", ex);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderUnavailableException(ProviderName, $"Cellulant OAuth returned {(int)response.StatusCode}: {responseBody}");

            var tokenResponse = JsonSerializer.Deserialize<CellulantTokenResponse>(responseBody);
            if (tokenResponse is null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "Cellulant OAuth returned no access_token");

            _accessToken = tokenResponse.AccessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 3600);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> SendAuthorisedAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Cellulant failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Cellulant {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "processed" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "rejected" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Cellulant API response shapes (internal) ===

    private sealed class CellulantTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }

    private sealed class CellulantCheckoutResponse
    {
        [JsonPropertyName("checkoutRequestId")] public string? CheckoutRequestId { get; set; }
        [JsonPropertyName("redirectUrl")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class CellulantPayoutResponse
    {
        [JsonPropertyName("transactionReference")] public string? TransactionReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class CellulantRefundResponse
    {
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class CellulantWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public CellulantWebhookData? Data { get; set; }
    }

    private sealed class CellulantWebhookData
    {
        [JsonPropertyName("checkoutRequestId")] public string? CheckoutRequestId { get; set; }
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
