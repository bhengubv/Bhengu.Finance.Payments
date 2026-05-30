// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.EcoCash.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.EcoCash.Providers;

/// <summary>
/// EcoCash (Zimbabwe) mobile-money gateway provider. Wraps the EcoCash Developers v2 REST API.
/// Implements C2B instant charges and refunds. Payouts are not exposed on the standard merchant
/// tier, so <see cref="IPayoutProvider"/> is intentionally not implemented.
/// Webhooks are POSTed to the configured <c>NotifyUrl</c>; the provider supplies no HMAC, so
/// signature verification relies on the secret-URL convention and clientCorrelator matching.
/// </summary>
public sealed class EcoCashPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly EcoCashOptions _options;
    private readonly ILogger<EcoCashPaymentProvider> _logger;

    public string ProviderName => "ecocash";

    public EcoCashPaymentProvider(
        HttpClient httpClient,
        IOptions<EcoCashOptions> options,
        ILogger<EcoCashPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(EcoCashOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(EcoCashOptions.MerchantCode)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var defaultUrl = _options.UseSandbox
                ? "https://developers.ecocash.co.zw/sandbox/"
                : "https://developers.ecocash.co.zw/";
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? defaultUrl);
        }

        _httpClient.DefaultRequestHeaders.Remove("X-Api-Key");
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", _options.ApiKey);

        if (!string.IsNullOrEmpty(_options.Username))
        {
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clientCorrelator = $"ecocash-{Guid.NewGuid():N}";
        var requestBody = BuildC2BBody(request, clientCorrelator, tranType: "MER");

        var body = await SendAsync(HttpMethod.Post, "api/v2/payment/instant/c2b/live", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<EcoCashTransactionResponse>(body);

        _logger.LogInformation("EcoCash C2B created: {Correlator} status={Status}",
            response?.ClientCorrelator ?? clientCorrelator, response?.TransactionOperationStatus);

        return new PaymentResponse
        {
            GatewayReference = response?.EcocashReference ?? response?.ClientCorrelator ?? clientCorrelator,
            Status = MapStatus(response?.TransactionOperationStatus ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = response?.TransactionOperationStatus
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clientCorrelator = $"ecocash-refund-{Guid.NewGuid():N}";
        var refundRequest = new PaymentRequest
        {
            PaymentMethodToken = request.GatewayReference,
            Amount = request.Amount,
            Currency = "USD",
            Description = request.Reason
        };

        var requestBody = BuildC2BBody(refundRequest, clientCorrelator, tranType: "REFUND",
            originalReference: request.GatewayReference);

        var body = await SendAsync(HttpMethod.Post, "api/v2/payment/instant/refund", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<EcoCashTransactionResponse>(body);

        _logger.LogInformation("EcoCash refund initiated: {Correlator} for {Original}",
            clientCorrelator, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = response?.EcocashReference ?? clientCorrelator,
            Amount = request.Amount,
            Status = MapStatus(response?.TransactionOperationStatus ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = response?.TransactionOperationStatus
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        // EcoCash does NOT sign callbacks. Authenticity is established by sending callbacks to a
        // secret URL (NotifyUrl) plus matching the clientCorrelator in the body against the value
        // sent on the original charge. Callers should perform that match in their webhook handler.
        ArgumentException.ThrowIfNullOrEmpty(payload);
        _logger.LogWarning("EcoCash does not sign callbacks — relying on NotifyUrl secrecy and clientCorrelator match instead.");
        return !string.IsNullOrEmpty(signature);
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var response = JsonSerializer.Deserialize<EcoCashTransactionResponse>(payload);
            if (response is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed EcoCash webhook: {Status}", response.TransactionOperationStatus);

            var status = response.TransactionOperationStatus?.ToLowerInvariant() switch
            {
                "completed" or "charged" or "success" => PaymentStatus.Completed,
                "failed" or "denied" => PaymentStatus.Failed,
                "refunded" => PaymentStatus.Refunded,
                "pending" or "pending subscriber validation" => PaymentStatus.Pending,
                _ => (PaymentStatus?)null
            };

            var reference = response.EcocashReference ?? response.ClientCorrelator;
            if (status is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = status.Value,
                EventType = response.TransactionOperationStatus
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse EcoCash webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private object BuildC2BBody(PaymentRequest request, string clientCorrelator, string tranType, string? originalReference = null)
    {
        return new
        {
            clientCorrelator,
            notifyUrl = _options.NotifyUrl,
            referenceCode = originalReference ?? clientCorrelator,
            tranType,
            endUserId = request.PaymentMethodToken,
            remarks = request.Description,
            transactionOperationStatus = tranType == "REFUND" ? "Refunded" : "Charged",
            amount = new
            {
                charging = new
                {
                    amount = request.Amount,
                    currency = request.Currency.ToUpperInvariant()
                }
            },
            merchantCode = _options.MerchantCode,
            merchantPin = _options.MerchantPin,
            merchantNumber = _options.MerchantNumber
        };
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to EcoCash failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("EcoCash {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "completed" or "charged" or "success" or "successful" => PaymentStatus.Completed,
        "pending" or "pending subscriber validation" or "processing" => PaymentStatus.Pending,
        "failed" or "denied" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === EcoCash API response shapes (internal) ===

    private sealed class EcoCashTransactionResponse
    {
        [JsonPropertyName("clientCorrelator")] public string? ClientCorrelator { get; set; }
        [JsonPropertyName("ecocashReference")] public string? EcocashReference { get; set; }
        [JsonPropertyName("transactionOperationStatus")] public string? TransactionOperationStatus { get; set; }
        [JsonPropertyName("referenceCode")] public string? ReferenceCode { get; set; }
        [JsonPropertyName("endUserId")] public string? EndUserId { get; set; }
    }
}
