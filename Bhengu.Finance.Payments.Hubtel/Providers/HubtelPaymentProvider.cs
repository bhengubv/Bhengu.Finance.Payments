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
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Hubtel.Providers;

/// <summary>
/// Hubtel (Ghana) payment gateway provider. Wraps the Hubtel hosted-checkout, refund and
/// send-money (payout) APIs. Auth is HTTP Basic with ClientId:ClientSecret. Webhook signature
/// is HMAC-SHA256 (hex) over the raw body, keyed by WebhookSecret.
/// </summary>
public sealed class HubtelPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly HubtelOptions _options;
    private readonly ILogger<HubtelPaymentProvider> _logger;

    public string ProviderName => ProviderNames.Hubtel;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney;

    public HubtelPaymentProvider(
        HttpClient httpClient,
        IOptions<HubtelOptions> options,
        ILogger<HubtelPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantAccountNumber))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(HubtelOptions.MerchantAccountNumber)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-txnstatus.hubtel.com/"
                : _options.BaseUrl ?? "https://api-txnstatus.hubtel.com/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new
        {
            totalAmount = request.Amount,
            description = request.Description,
            callbackUrl = _options.CallbackUrl,
            returnUrl = _options.ReturnUrl,
            merchantAccountNumber = _options.MerchantAccountNumber,
            cancellationUrl = _options.ReturnUrl,
            clientReference = request.PaymentMethodToken,
            payeeName = request.Metadata?.GetValueOrDefault("payeeName") ?? "",
            payeeMobileNumber = request.Metadata?.GetValueOrDefault("payeeMobileNumber") ?? "",
            payeeEmail = request.Metadata?.GetValueOrDefault("payeeEmail") ?? ""
        };

        var responseBody = await SendAsync(HttpMethod.Post, "checkout/initiate", body, ct, "ProcessPayment").ConfigureAwait(false);
        var checkout = JsonSerializer.Deserialize<HubtelCheckoutResponse>(responseBody);

        _logger.LogInformation("Hubtel checkout initiated: id={Id} url={Url}",
            checkout?.Data?.CheckoutId, checkout?.Data?.CheckoutUrl);

        return new PaymentResponse
        {
            GatewayReference = checkout?.Data?.CheckoutId ?? request.PaymentMethodToken,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = checkout?.Data?.CheckoutUrl,
            Message = checkout?.Message
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new
        {
            transactionId = request.GatewayReference,
            amount = request.Amount,
            reason = request.Reason,
            clientReference = $"rf-{Guid.NewGuid():N}"
        };

        var responseBody = await SendAsync(HttpMethod.Post, "transactions/refund", body, ct, "ProcessRefund").ConfigureAwait(false);
        var refund = JsonSerializer.Deserialize<HubtelRefundResponse>(responseBody);

        _logger.LogInformation("Hubtel refund: id={Id} status={Status}", refund?.Data?.TransactionId, refund?.Data?.Status);

        return new RefundResponse
        {
            GatewayReference = refund?.Data?.TransactionId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refund?.Data?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refund?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken format: "<channel>:<msisdn>" e.g. "mtn-gh:233244000000"
        var colon = request.DestinationToken.IndexOf(':');
        if (colon <= 0)
            throw new BhenguPaymentException(ProviderName,
                "Hubtel PayoutRequest.DestinationToken must be '<channel>:<msisdn>' (channel one of mtn-gh|vodafone-gh|tigo-gh)");

        var channel = request.DestinationToken[..colon];
        var msisdn = request.DestinationToken[(colon + 1)..];
        var clientReference = $"po-{Guid.NewGuid():N}";

        var body = new
        {
            RecipientName = request.Description,
            RecipientMsisdn = msisdn,
            CustomerEmail = "",
            Channel = channel,
            Amount = request.Amount,
            PrimaryCallbackUrl = _options.CallbackUrl,
            Description = request.Description,
            ClientReference = clientReference
        };

        var path = $"merchantaccount/merchants/{Uri.EscapeDataString(_options.MerchantAccountNumber)}/send/mobilemoney";
        var responseBody = await SendAsync(HttpMethod.Post, path, body, ct, "ProcessPayout").ConfigureAwait(false);
        var payout = JsonSerializer.Deserialize<HubtelPayoutResponse>(responseBody);

        _logger.LogInformation("Hubtel send-money payout: id={Id} status={Status} channel={Channel}",
            payout?.Data?.TransactionId, payout?.Data?.TransactionStatus, channel);

        return new PayoutResponse
        {
            GatewayReference = payout?.Data?.TransactionId ?? clientReference,
            Status = MapStatus(payout?.Data?.TransactionStatus ?? "pending"),
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
            _logger.LogWarning("Hubtel WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var hex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(hex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Hubtel webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var evt = JsonSerializer.Deserialize<HubtelWebhookEvent>(payload);
            var data = evt?.Data;
            if (data is null || string.IsNullOrEmpty(data.ClientReference ?? data.TransactionId))
                return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Hubtel webhook event: type={Type} status={Status}", evt?.Type, data.Status);

            var status = (evt?.Type?.ToLowerInvariant(), data.Status?.ToLowerInvariant()) switch
            {
                (_, "success") or (_, "paid") or (_, "completed") => PaymentStatus.Completed,
                (_, "failed") or (_, "declined") => PaymentStatus.Failed,
                (_, "cancelled") or (_, "canceled") => PaymentStatus.Cancelled,
                (_, "refunded") => PaymentStatus.Refunded,
                ("refund.completed", _) => PaymentStatus.Refunded,
                ("payout.completed", _) => PaymentStatus.Completed,
                _ => (PaymentStatus?)null
            };

            if (status is null) return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = data.ClientReference ?? data.TransactionId!,
                Status = status.Value,
                EventType = evt?.Type ?? data.Status
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Hubtel webhook event");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Hubtel failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Hubtel {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "paid" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private sealed class HubtelCheckoutResponse
    {
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelCheckoutData? Data { get; set; }
    }

    private sealed class HubtelCheckoutData
    {
        [JsonPropertyName("checkoutUrl")] public string? CheckoutUrl { get; set; }
        [JsonPropertyName("checkoutId")] public string? CheckoutId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
    }

    private sealed class HubtelRefundResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelRefundData? Data { get; set; }
    }

    private sealed class HubtelRefundData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class HubtelPayoutResponse
    {
        [JsonPropertyName("responseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public HubtelPayoutData? Data { get; set; }
    }

    private sealed class HubtelPayoutData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
        [JsonPropertyName("transactionStatus")] public string? TransactionStatus { get; set; }
    }

    private sealed class HubtelWebhookEvent
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("data")] public HubtelWebhookData? Data { get; set; }
    }

    private sealed class HubtelWebhookData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("clientReference")] public string? ClientReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }
}
