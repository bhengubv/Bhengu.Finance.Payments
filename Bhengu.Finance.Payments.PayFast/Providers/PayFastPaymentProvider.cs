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
using Bhengu.Finance.Payments.PayFast.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayFast.Providers;

/// <summary>
/// PayFast (South Africa) payment gateway provider.
/// Supports tokenised ad-hoc charging via the PayFast subscriptions API.
/// PayFast does NOT support payouts via API — <see cref="IPayoutProvider"/> is intentionally not implemented.
/// </summary>
public sealed class PayFastPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayFastOptions _options;
    private readonly ILogger<PayFastPaymentProvider> _logger;

    public string ProviderName => "payfast";

    public PayFastPaymentProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        ILogger<PayFastPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayFastOptions.MerchantId)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? "https://sandbox.payfast.co.za/"
                : "https://api.payfast.co.za/");
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amountInCents = (int)(request.Amount * 100);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");

        var formData = new Dictionary<string, string>
        {
            ["amount"] = amountInCents.ToString(),
            ["item_name"] = request.Description
        };

        if (request.Metadata is not null)
        {
            if (request.Metadata.TryGetValue("payment_id", out var paymentId))
                formData["m_payment_id"] = paymentId;
            else if (request.Metadata.TryGetValue("transaction_id", out var transactionId))
                formData["m_payment_id"] = transactionId;
        }

        var signature = GenerateApiSignature(formData, timestamp);

        var path = $"subscriptions/{Uri.EscapeDataString(request.PaymentMethodToken)}/adhoc{(_options.UseSandbox ? "?testing=true" : "")}";

        using var http = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(formData)
        };
        http.Headers.Add("merchant-id", _options.MerchantId);
        http.Headers.Add("version", "v1");
        http.Headers.Add("timestamp", timestamp);
        http.Headers.Add("signature", signature);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(http, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PayFast failed", ex);
        }

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PayFast payment failed: {StatusCode} {Body}", response.StatusCode, body);
            // 4xx that isn't 429 — treat as a decline (insufficient funds, card error, etc.)
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), body);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {body}");
        }

        var payfastResponse = JsonSerializer.Deserialize<PayFastAdhocResponse>(body);
        var status = MapStatus(payfastResponse?.data?.response ?? "pending");

        _logger.LogInformation("PayFast ad-hoc payment created: {GatewayReference} status={Status}",
            payfastResponse?.data?.pf_payment_id, status);

        return new PaymentResponse
        {
            GatewayReference = payfastResponse?.data?.pf_payment_id ?? string.Empty,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = payfastResponse?.data?.response_reason
        };
    }

    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // PayFast does not expose a refund API — refunds are processed manually via merchant dashboard.
        // We return a deterministic tracking reference so the caller can match this entry to the manual
        // action when reconciling. Consumers requiring automated refunds must use a different provider.
        _logger.LogWarning(
            "PayFast refund requested for {GatewayReference} amount={Amount}. PayFast has no refund API; manual dashboard processing required.",
            request.GatewayReference, request.Amount);

        var trackingReference = $"PAYFAST-MANUAL-REFUND-{request.GatewayReference}-{DateTime.UtcNow:yyyyMMddHHmmss}";

        return Task.FromResult(new RefundResponse
        {
            GatewayReference = trackingReference,
            Amount = request.Amount,
            Status = PaymentStatus.Pending,
            ProcessedAt = DateTime.UtcNow,
            Message = "PayFast refunds require manual processing via the merchant dashboard."
        });
    }

    /// <summary>
    /// Verifies a PayFast ITN webhook signature using MD5 of alphabetically-sorted parameters + passphrase.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        try
        {
            var parameters = ParseFormUrlEncoded(payload);
            parameters.Remove("signature");

            var sorted = parameters
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={WebUtility.UrlEncode(p.Value)}")
                .ToList();

            if (!string.IsNullOrEmpty(_options.Passphrase))
                sorted.Add($"passphrase={WebUtility.UrlEncode(_options.Passphrase)}");

            var paramString = string.Join("&", sorted);
            var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(paramString));
            var computed = Convert.ToHexString(hashBytes).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computed));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayFast signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var parameters = ParseFormUrlEncoded(payload);

            var pfPaymentId = parameters.GetValueOrDefault("pf_payment_id", string.Empty);
            var paymentStatus = parameters.GetValueOrDefault("payment_status", string.Empty);
            var status = MapStatus(paymentStatus);

            _logger.LogInformation("PayFast ITN parsed: gatewayReference={PfPaymentId} status={Status}",
                pfPaymentId, status);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = pfPaymentId,
                Status = status,
                EventType = "payfast.itn",
                RawPayload = parameters
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse PayFast ITN payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // === PayFast-specific extensions (not on IPaymentGatewayProvider) ===

    /// <summary>Fetch details of a tokenisation agreement (ad-hoc subscription).</summary>
    public async Task<PayFastTokenInfo?> FetchTokenAsync(string token, CancellationToken ct = default)
    {
        return await SendSignedAsync<PayFastFetchResponse>(
            HttpMethod.Get, $"subscriptions/{token}/fetch", ct)
            .ConfigureAwait(false) is { } r ? r.data : null;
    }

    /// <summary>Cancel a tokenisation agreement.</summary>
    public async Task<bool> CancelTokenAsync(string token, CancellationToken ct = default)
    {
        try
        {
            await SendSignedAsync<object>(HttpMethod.Put, $"subscriptions/{token}/cancel", ct).ConfigureAwait(false);
            _logger.LogInformation("PayFast token cancelled: {Token}", token);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayFast cancel token failed for {Token}", token);
            return false;
        }
    }

    /// <summary>Query a transaction by ID.</summary>
    public Task<PayFastTransactionQuery?> QueryTransactionAsync(string transactionIdOrPaymentId, CancellationToken ct = default)
        => SendSignedAsync<PayFastTransactionQuery>(HttpMethod.Get, $"process/query/{transactionIdOrPaymentId}", ct);

    private async Task<T?> SendSignedAsync<T>(HttpMethod method, string relativePath, CancellationToken ct) where T : class
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss");
        var signature = GenerateApiSignature(new Dictionary<string, string>(), timestamp);

        var url = relativePath + (_options.UseSandbox ? (relativePath.Contains('?') ? "&testing=true" : "?testing=true") : "");
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Add("merchant-id", _options.MerchantId);
        req.Headers.Add("version", "v1");
        req.Headers.Add("timestamp", timestamp);
        req.Headers.Add("signature", signature);

        HttpResponseMessage resp;
        try
        {
            resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "PayFast API HTTP failure", ex);
        }

        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("PayFast {Method} {Path} failed: {Status} {Body}", method, relativePath, resp.StatusCode, body);
            return null;
        }
        return JsonSerializer.Deserialize<T>(body);
    }

    private string GenerateApiSignature(Dictionary<string, string> bodyParams, string timestamp)
    {
        var allParams = new Dictionary<string, string>
        {
            ["merchant-id"] = _options.MerchantId,
            ["passphrase"] = _options.Passphrase,
            ["timestamp"] = timestamp,
            ["version"] = "v1"
        };
        foreach (var (k, v) in bodyParams)
            allParams[k] = v;

        var sorted = allParams
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}");

        var paramString = string.Join("&", sorted);
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(paramString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static Dictionary<string, string> ParseFormUrlEncoded(string formData)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in formData.Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2)
                result[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return result;
    }

    private static PaymentStatus MapStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "APPROVED" or "COMPLETE" or "COMPLETED" => PaymentStatus.Completed,
        "PENDING" => PaymentStatus.Pending,
        "FAILED" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === PayFast API response shapes (internal) ===

    private sealed class PayFastAdhocResponse
    {
        public int code { get; set; }
        public string? status { get; set; }
        public PayFastAdhocData? data { get; set; }
    }

    private sealed class PayFastAdhocData
    {
        public bool message { get; set; }
        public string? pf_payment_id { get; set; }
        public string? response { get; set; }
        public string? response_reason { get; set; }
    }

    private sealed class PayFastFetchResponse
    {
        public int code { get; set; }
        public string? status { get; set; }
        public PayFastTokenInfo? data { get; set; }
    }
}

/// <summary>PayFast tokenisation agreement details.</summary>
public sealed class PayFastTokenInfo
{
    [JsonPropertyName("token")] public string? Token { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("status_text")] public string? StatusText { get; set; }
    [JsonPropertyName("status_reason")] public string? StatusReason { get; set; }
}

/// <summary>PayFast transaction query response.</summary>
public sealed class PayFastTransactionQuery
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("data")] public PayFastTransactionData? Data { get; set; }
}

public sealed class PayFastTransactionData
{
    [JsonPropertyName("pf_payment_id")] public string? PfPaymentId { get; set; }
    [JsonPropertyName("payment_status")] public string? PaymentStatus { get; set; }
    [JsonPropertyName("amount_gross")] public decimal AmountGross { get; set; }
    [JsonPropertyName("amount_fee")] public decimal AmountFee { get; set; }
    [JsonPropertyName("amount_net")] public decimal AmountNet { get; set; }
}
