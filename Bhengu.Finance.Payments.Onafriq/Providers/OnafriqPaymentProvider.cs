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
using Bhengu.Finance.Payments.Onafriq.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Onafriq.Providers;

/// <summary>
/// Onafriq (formerly MFS Africa) cross-border mobile-money provider. Onafriq is primarily a
/// transfer / disbursement rail (wallet-to-wallet across 35+ African countries) — the payout path
/// is the canonical use. ProcessPaymentAsync maps to the <c>/v1/collections</c> endpoint.
/// Refunds are not supported by Onafriq: money movement is one-directional and reversals require
/// a new opposite transaction.
/// </summary>
public sealed class OnafriqPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly OnafriqOptions _options;
    private readonly ILogger<OnafriqPaymentProvider> _logger;

    public string ProviderName => ProviderNames.Onafriq;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.CrossBorder;

    public OnafriqPaymentProvider(
        HttpClient httpClient,
        IOptions<OnafriqOptions> options,
        ILogger<OnafriqPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OnafriqOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OnafriqOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://api-sandbox.onafriq.com/"
                : "https://api.onafriq.com/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }

        _httpClient.DefaultRequestHeaders.Remove("X-API-Key");
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // PaymentMethodToken format: "<country>:<walletNumber>" (e.g. "ZA:27710000000").
        var (countryCode, walletNumber) = SplitDestination(request.PaymentMethodToken, defaultCountry: "ZA");

        var requestBody = new
        {
            merchantId = _options.MerchantId,
            source = new
            {
                type = "wallet",
                country = countryCode,
                walletNumber
            },
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            reference = $"col-{Guid.NewGuid():N}",
            description = request.Description,
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "v1/collections", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<OnafriqTransactionResponse>(body);

        _logger.LogInformation("Onafriq collection initiated: {Id} status={Status}",
            response?.TransactionId, response?.Status);

        return new PaymentResponse
        {
            GatewayReference = response?.TransactionId ?? string.Empty,
            Status = MapStatus(response?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = response?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken format: "<country>:<walletNumber>" (e.g. "GH:233244000000").
        var (destCountry, destWallet) = SplitDestination(request.DestinationToken, defaultCountry: "GH");

        var requestBody = new
        {
            merchantId = _options.MerchantId,
            source = new
            {
                type = "wallet",
                country = "ZA",
                walletNumber = _options.MerchantId
            },
            destination = new
            {
                type = "wallet",
                country = destCountry,
                walletNumber = destWallet
            },
            amount = request.Amount,
            currency = request.Currency.ToUpperInvariant(),
            reference = $"pay-{Guid.NewGuid():N}",
            description = request.Description,
            callbackUrl = _options.CallbackUrl
        };

        var body = await SendAsync(HttpMethod.Post, "v1/transactions", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<OnafriqTransactionResponse>(body);

        _logger.LogInformation("Onafriq transfer initiated: {Id} status={Status}",
            response?.TransactionId, response?.Status);

        return new PayoutResponse
        {
            GatewayReference = response?.TransactionId ?? string.Empty,
            Status = MapStatus(response?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        // Onafriq money movement is one-directional. There is no /refund endpoint; reversals must
        // be performed as a new opposite transaction (a payout from your merchant wallet to the
        // original payer's wallet). Surface this explicitly so callers do not silently lose money.
        throw new BhenguPaymentException(
            ProviderName,
            "Onafriq does not support refunds; reversals require a new opposite transaction. " +
            "Issue a payout from your merchant wallet back to the original payer's wallet instead.",
            providerErrorCode: "refund_unsupported");
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Onafriq WebhookSecret not configured — signature verification cannot succeed.");
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
            _logger.LogError(ex, "Onafriq webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<OnafriqWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Onafriq webhook event: {EventType}", webhookEvent.EventType);

            var status = webhookEvent.EventType?.ToLowerInvariant() switch
            {
                "transaction.completed" or "transaction.successful" => PaymentStatus.Completed,
                "transaction.failed" or "transaction.rejected" => PaymentStatus.Failed,
                "transaction.pending" => PaymentStatus.Pending,
                "collection.completed" => PaymentStatus.Completed,
                "collection.failed" => PaymentStatus.Failed,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(webhookEvent.Data?.TransactionId))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = webhookEvent.Data.TransactionId,
                Status = status.Value,
                EventType = webhookEvent.EventType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Onafriq webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static (string Country, string Wallet) SplitDestination(string token, string defaultCountry)
    {
        var colon = token.IndexOf(':');
        if (colon <= 0)
            return (defaultCountry, token);
        return (token[..colon], token[(colon + 1)..]);
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Onafriq failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Onafriq {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "completed" or "successful" or "success" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" or "submitted" => PaymentStatus.Pending,
        "failed" or "rejected" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // === Onafriq API response shapes (internal) ===

    private sealed class OnafriqTransactionResponse
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
    }

    private sealed class OnafriqWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public OnafriqWebhookData? Data { get; set; }
    }

    private sealed class OnafriqWebhookData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }
}
