// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Pesapal.Providers;

/// <summary>
/// Pesapal (Kenya / East Africa) payment gateway provider. Wraps Pesapal API 3.0.
/// Auth is a short-lived Bearer token (5-min TTL) obtained via /api/Auth/RequestToken.
/// Webhook IPNs are NOT signed by Pesapal — <see cref="VerifyWebhookSignature"/> returns true
/// when payload and signature are non-empty and the configured ConsumerSecret matches the
/// provided signature; production callers should ALSO call /api/Transactions/GetTransactionStatus
/// using the OrderTrackingId to confirm settlement (the canonical Pesapal hardening).
/// Pesapal does NOT expose payouts on the standard merchant API.
/// </summary>
public sealed class PesapalPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly PesapalOptions _options;
    private readonly ILogger<PesapalPaymentProvider> _logger;
    private string? _cachedToken;
    private DateTime _tokenExpiresAtUtc;

    public string ProviderName => ProviderNames.Pesapal;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney;

    public PesapalPaymentProvider(
        HttpClient httpClient,
        IOptions<PesapalOptions> options,
        ILogger<PesapalPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.ConsumerSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var liveDefault = _options.BaseUrl ?? "https://pay.pesapal.com/v3";
            var sandboxDefault = _options.SandboxUrl ?? "https://cybqa.pesapal.com/pesapalv3";
            var raw = _options.UseSandbox ? sandboxDefault : liveDefault;
            // Always end with a trailing slash so relative paths resolve cleanly.
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.IpnId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PesapalOptions.IpnId)} is required (register an IPN via /api/URLSetup/RegisterIPN first)");

        var email = request.Metadata?.GetValueOrDefault("email") ?? "";
        var phone = request.Metadata?.GetValueOrDefault("phone_number") ?? "";
        var country = request.Metadata?.GetValueOrDefault("country_code") ?? "KE";
        var firstName = request.Metadata?.GetValueOrDefault("first_name") ?? "";
        var lastName = request.Metadata?.GetValueOrDefault("last_name") ?? "";

        var body = new
        {
            id = request.PaymentMethodToken,
            currency = request.Currency.ToUpperInvariant(),
            amount = request.Amount,
            description = request.Description,
            callback_url = _options.CallbackUrl,
            notification_id = _options.IpnId,
            billing_address = new
            {
                email_address = email,
                phone_number = phone,
                country_code = country,
                first_name = firstName,
                last_name = lastName
            }
        };

        var responseBody = await SendAsync(HttpMethod.Post, "api/Transactions/SubmitOrderRequest", body, ct, "ProcessPayment").ConfigureAwait(false);
        var submit = JsonSerializer.Deserialize<PesapalSubmitOrderResponse>(responseBody);

        _logger.LogInformation("Pesapal order submitted: tracking={Tracking} merchantRef={MerchantRef}",
            submit?.OrderTrackingId, submit?.MerchantReference);

        return new PaymentResponse
        {
            GatewayReference = submit?.OrderTrackingId ?? request.PaymentMethodToken,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = submit?.RedirectUrl,
            Message = submit?.Status
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new
        {
            confirmation_code = request.GatewayReference,
            amount = request.Amount,
            username = _options.ConsumerKey,
            remarks = request.Reason
        };

        var responseBody = await SendAsync(HttpMethod.Post, "api/Transactions/RefundRequest", body, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<PesapalRefundResponse>(responseBody);

        _logger.LogInformation("Pesapal refund: status={Status} message={Message}", refund?.Status, refund?.Message);

        var mapped = string.Equals(refund?.Status, "200", StringComparison.Ordinal)
            ? PaymentStatus.Refunded
            : PaymentStatus.Failed;

        return new RefundResponse
        {
            GatewayReference = request.GatewayReference,
            Amount = request.Amount,
            Status = mapped,
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Message
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
        {
            _logger.LogWarning("Pesapal ConsumerSecret not configured — IPN verification cannot proceed.");
            return false;
        }

        // Pesapal does NOT HMAC IPN payloads. We perform a constant-time equality check between
        // the supplied signature and the configured ConsumerSecret so callers MUST send a known
        // shared secret in their reverse-proxy header. Production callers should additionally call
        // GetTransactionStatus(OrderTrackingId) for canonical confirmation.
        var a = Encoding.UTF8.GetBytes(signature);
        var b = Encoding.UTF8.GetBytes(_options.ConsumerSecret);
        if (a.Length != b.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var ipn = JsonSerializer.Deserialize<PesapalIpnEvent>(payload);
            if (ipn is null || string.IsNullOrEmpty(ipn.OrderTrackingId))
                return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Pesapal IPN: tracking={Tracking} notifType={NotifType}",
                ipn.OrderTrackingId, ipn.OrderNotificationType);

            var status = ipn.OrderNotificationType?.ToUpperInvariant() switch
            {
                "IPNCHANGE" or "CHANGE" or "COMPLETED" => PaymentStatus.Pending,
                _ => (PaymentStatus?)PaymentStatus.Pending
            };

            if (status is null) return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = ipn.OrderTrackingId,
                Status = status.Value,
                EventType = ipn.OrderNotificationType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Pesapal IPN");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> EnsureTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiresAtUtc)
            return _cachedToken!;

        var body = new { consumer_key = _options.ConsumerKey, consumer_secret = _options.ConsumerSecret };
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/Auth/RequestToken")
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Pesapal failed", ex);
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

        var auth = JsonSerializer.Deserialize<PesapalAuthResponse>(responseBody);
        if (string.IsNullOrWhiteSpace(auth?.Token))
            throw new ProviderUnavailableException(ProviderName, "Pesapal RequestToken returned an empty token");

        _cachedToken = auth!.Token;
        // Pesapal tokens live ~5 minutes — refresh at 4.5 min to be safe.
        _tokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(270);
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

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Pesapal failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Pesapal {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    // === Pesapal API response shapes (internal) ===

    private sealed class PesapalAuthResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expiryDate")] public string? ExpiryDate { get; set; }
    }

    private sealed class PesapalSubmitOrderResponse
    {
        [JsonPropertyName("order_tracking_id")] public string? OrderTrackingId { get; set; }
        [JsonPropertyName("merchant_reference")] public string? MerchantReference { get; set; }
        [JsonPropertyName("redirect_url")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class PesapalRefundResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class PesapalIpnEvent
    {
        [JsonPropertyName("OrderTrackingId")] public string? OrderTrackingId { get; set; }
        [JsonPropertyName("OrderMerchantReference")] public string? OrderMerchantReference { get; set; }
        [JsonPropertyName("OrderNotificationType")] public string? OrderNotificationType { get; set; }
    }
}
