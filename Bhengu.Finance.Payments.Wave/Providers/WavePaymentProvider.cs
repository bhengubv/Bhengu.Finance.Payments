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
using Bhengu.Finance.Payments.Wave.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Wave.Providers;

/// <summary>
/// Wave (Senegal / Cote d'Ivoire / Mali / Uganda) payment provider. Wraps the Wave Business
/// REST API: Checkout Sessions for collections, Payouts for disbursements, and HMAC-signed
/// webhooks via the <c>Wave-Signature</c> header (format <c>t=...,v1=...</c>).
/// </summary>
public sealed class WavePaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly WaveOptions _options;
    private readonly ILogger<WavePaymentProvider> _logger;

    public string ProviderName => ProviderNames.Wave;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.RedirectFlow;

    public WavePaymentProvider(
        HttpClient httpClient,
        IOptions<WaveOptions> options,
        ILogger<WavePaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(WaveOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.wave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = new
        {
            amount = request.Amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant(),
            error_url = _options.ErrorUrl,
            success_url = _options.SuccessUrl,
            client_reference = request.PaymentMethodToken
        };

        var body = await SendAsync(HttpMethod.Post, "v1/checkout/sessions", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var waveResponse = JsonSerializer.Deserialize<WaveCheckoutResponse>(body);

        _logger.LogInformation("Wave checkout session created: {Id} status={Status}", waveResponse?.Id, waveResponse?.CheckoutStatus);

        return new PaymentResponse
        {
            GatewayReference = waveResponse?.Id ?? string.Empty,
            Status = MapStatus(waveResponse?.CheckoutStatus ?? waveResponse?.PaymentStatus ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = waveResponse?.WaveLaunchUrl,
            Message = waveResponse?.CheckoutStatus
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken format: "<countryCode>:<phone>" (e.g. "SN:221761234567") OR raw phone.
        string countryCode = "SN";
        string phone = request.DestinationToken;
        var colon = request.DestinationToken.IndexOf(':');
        if (colon > 0)
        {
            countryCode = request.DestinationToken[..colon];
            phone = request.DestinationToken[(colon + 1)..];
        }

        var requestBody = new
        {
            receive_amount = request.Amount.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            mobile = new
            {
                national_id = phone,
                country_code = countryCode
            },
            name = request.Description,
            client_reference = $"payout-{Guid.NewGuid():N}"
        };

        var body = await SendAsync(HttpMethod.Post, "v1/payouts", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var payoutResponse = JsonSerializer.Deserialize<WavePayoutResponse>(body);

        _logger.LogInformation("Wave payout created: {Id} status={Status}", payoutResponse?.Id, payoutResponse?.Status);

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.Id ?? string.Empty,
            Status = MapStatus(payoutResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = $"v1/checkout/sessions/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, new { }, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<WaveCheckoutResponse>(body);

        _logger.LogInformation("Wave refund issued for session {SessionId}", request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Id ?? request.GatewayReference,
            Amount = request.Amount,
            Status = PaymentStatus.Refunded,
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.CheckoutStatus
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Wave WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            // Wave-Signature header: "t=<timestamp>,v1=<signature>"
            string? timestamp = null;
            string? sentSig = null;
            foreach (var part in signature.Split(','))
            {
                var idx = part.IndexOf('=');
                if (idx < 0) continue;
                var key = part[..idx].Trim();
                var value = part[(idx + 1)..].Trim();
                if (key == "t") timestamp = value;
                else if (key == "v1") sentSig = value;
            }

            if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(sentSig))
                return false;

            var signedPayload = $"{timestamp}.{payload}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(signedPayload));
            var computedSignature = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(sentSig.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedSignature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Wave webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<WaveWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Wave webhook event: {EventType}", webhookEvent.Type);

            var status = webhookEvent.Type?.ToLowerInvariant() switch
            {
                "checkout.session.completed" or "checkout.session.payment_succeeded" => PaymentStatus.Completed,
                "checkout.session.payment_failed" => PaymentStatus.Failed,
                "merchant.payment_refunded" or "checkout.session.refunded" => PaymentStatus.Refunded,
                "payout.completed" => PaymentStatus.Completed,
                "payout.failed" => PaymentStatus.Failed,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(webhookEvent.Data?.Id))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhookEvent.Data.Id,
                Status = status.Value,
                EventType = webhookEvent.Type
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Wave webhook event");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Wave failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Wave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "complete" or "completed" or "succeeded" or "successful" or "processing_successful" => PaymentStatus.Completed,
        "open" or "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "expired" or "processing_failed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Wave API response shapes (internal) ===

    private sealed class WaveCheckoutResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("checkout_status")] public string? CheckoutStatus { get; set; }
        [JsonPropertyName("payment_status")] public string? PaymentStatus { get; set; }
        [JsonPropertyName("wave_launch_url")] public string? WaveLaunchUrl { get; set; }
        [JsonPropertyName("client_reference")] public string? ClientReference { get; set; }
    }

    private sealed class WavePayoutResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("receive_amount")] public string? ReceiveAmount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class WaveWebhookEvent
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("data")] public WaveCheckoutResponse? Data { get; set; }
    }
}
