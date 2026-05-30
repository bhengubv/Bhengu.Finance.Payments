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
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Flutterwave.Providers;

/// <summary>
/// Flutterwave pan-African payment provider. Wraps the Flutterwave v3 REST API and supports
/// payment initialisation (<c>/v3/payments</c>), transfers (<c>/v3/transfers</c>) for payouts,
/// and refunds. Webhook authenticity is checked via constant-time comparison of the
/// <c>verif-hash</c> header against the configured WebhookSecret (Flutterwave does not HMAC).
/// </summary>
public sealed class FlutterwavePaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;
    private readonly ILogger<FlutterwavePaymentProvider> _logger;

    public string ProviderName => "flutterwave";

    public FlutterwavePaymentProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwavePaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FlutterwaveOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.flutterwave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var email = request.Metadata?.GetValueOrDefault("email");
        if (string.IsNullOrWhiteSpace(email))
            throw new PaymentDeclinedException(ProviderName, "missing_email",
                "Flutterwave requires an 'email' in PaymentRequest.Metadata.");

        var name = request.Metadata?.GetValueOrDefault("name") ?? email;
        var phone = request.Metadata?.GetValueOrDefault("phonenumber");

        var requestBody = new
        {
            tx_ref = request.PaymentMethodToken,
            amount = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            redirect_url = _options.RedirectUrl,
            customer = new
            {
                email,
                name,
                phonenumber = phone
            },
            customizations = new
            {
                title = request.Description
            }
        };

        var body = await SendAsync(HttpMethod.Post, "v3/payments", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var fwResponse = JsonSerializer.Deserialize<FlutterwavePaymentResponse>(body);

        _logger.LogInformation("Flutterwave payment initialised: {TxRef} status={Status}",
            request.PaymentMethodToken, fwResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = request.PaymentMethodToken,
            Status = MapStatus(fwResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = fwResponse?.Data?.Link ?? fwResponse?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken format: "<bankCode>:<accountNumber>" (e.g. "044:0690000040").
        var colon = request.DestinationToken.IndexOf(':');
        if (colon <= 0)
            throw new PaymentDeclinedException(ProviderName, "invalid_destination",
                "Flutterwave PayoutRequest.DestinationToken must be 'bankCode:accountNumber'.");

        var bankCode = request.DestinationToken[..colon];
        var accountNumber = request.DestinationToken[(colon + 1)..];

        var reference = $"transfer-{Guid.NewGuid():N}";
        var requestBody = new
        {
            account_bank = bankCode,
            account_number = accountNumber,
            amount = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            narration = request.Description,
            currency = request.Currency.ToUpperInvariant(),
            reference,
            beneficiary_name = request.Description
        };

        var body = await SendAsync(HttpMethod.Post, "v3/transfers", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var transferResponse = JsonSerializer.Deserialize<FlutterwaveTransferResponse>(body);

        _logger.LogInformation("Flutterwave transfer initialised: {Reference} status={Status}",
            transferResponse?.Data?.Reference ?? reference, transferResponse?.Data?.Status);

        return new PayoutResponse
        {
            GatewayReference = transferResponse?.Data?.Reference ?? reference,
            Status = MapStatus(transferResponse?.Data?.Status ?? "pending"),
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
            amount = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
        };

        // request.GatewayReference is expected to be the Flutterwave transaction id (numeric).
        var path = $"v3/transactions/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<FlutterwaveRefundResponse>(body);

        _logger.LogInformation("Flutterwave refund created: {RefundId} for transaction {TransactionId}",
            refundResponse?.Data?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Data?.Id ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refundResponse?.Data?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Message
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Flutterwave WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            // Flutterwave does NOT HMAC the body; it sends the configured secret verbatim in the
            // verif-hash header. Constant-time compare to defeat timing-based equality leaks.
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(_options.WebhookSecret));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Flutterwave webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<FlutterwaveWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Flutterwave webhook event: {EventType}", webhookEvent.Event);

            var status = webhookEvent.Event?.ToLowerInvariant() switch
            {
                "charge.completed" or "charge.complete" => PaymentStatus.Completed,
                "transfer.completed" => PaymentStatus.Completed,
                "charge.failed" or "transfer.failed" => PaymentStatus.Failed,
                "refund.completed" or "refund.created" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            var reference = webhookEvent.Data?.TxRef ?? webhookEvent.Data?.Reference;
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
            _logger.LogError(ex, "Failed to parse Flutterwave webhook event");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Flutterwave failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Flutterwave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "new" => PaymentStatus.Completed,
        "pending" or "processing" or "initialised" => PaymentStatus.Pending,
        "failed" or "abandoned" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Flutterwave API response shapes (internal) ===

    private sealed class FlutterwavePaymentResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwavePaymentData? Data { get; set; }
    }

    private sealed class FlutterwavePaymentData
    {
        [JsonPropertyName("link")] public string? Link { get; set; }
    }

    private sealed class FlutterwaveTransferResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwaveTransferData? Data { get; set; }
    }

    private sealed class FlutterwaveTransferData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class FlutterwaveRefundResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public FlutterwaveRefundData? Data { get; set; }
    }

    private sealed class FlutterwaveRefundData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount_refunded")] public decimal AmountRefunded { get; set; }
    }

    private sealed class FlutterwaveWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public FlutterwaveWebhookData? Data { get; set; }
    }

    private sealed class FlutterwaveWebhookData
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tx_ref")] public string? TxRef { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }
}
