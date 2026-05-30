// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
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
using Bhengu.Finance.Payments.Remita.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Remita.Providers;

/// <summary>
/// Remita (SystemSpecs) payment + payout provider. Wraps the Remita REST surface for
/// Nigerian government revenue collection, corporate disbursement, e-collection, and
/// Single Send Money payouts. Authentication uses SHA-512 hex hashes of concatenated
/// fields with the configured API key — Remita does NOT use bearer tokens for these endpoints.
/// </summary>
public sealed class RemitaPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private const string PaymentInitPath =
        "remita/exapp/api/v1/send/api/echannelsvc/merchant/api/paymentinit";

    private const string SendMoneyPath =
        "remita/exapp/api/v1/send/api/echannelsvc/merchant/api/sendmoney";

    private const string RefundPath = "remita/refundservice/refund/initiate";

    private readonly HttpClient _httpClient;
    private readonly RemitaOptions _options;
    private readonly ILogger<RemitaPaymentProvider> _logger;

    public string ProviderName => ProviderNames.Remita;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.BankTransfer;

    public RemitaPaymentProvider(
        HttpClient httpClient,
        IOptions<RemitaOptions> options,
        ILogger<RemitaPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ServiceTypeId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.ServiceTypeId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://remitademo.net"
                : _options.BaseUrl ?? "https://login.remita.net";
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderId = request.PaymentMethodToken;
        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

        // Remita payment-init hash = SHA512(merchantId + serviceTypeId + orderId + total + apiKey).
        var hash = Sha512Hex(
            _options.MerchantId + _options.ServiceTypeId + orderId + amount + _options.ApiKey);

        var requestBody = new
        {
            serviceTypeId = _options.ServiceTypeId,
            amount,
            orderId,
            payerName = request.Metadata?.GetValueOrDefault("payerName") ?? "Bhengu Customer",
            payerEmail = request.Metadata?.GetValueOrDefault("payerEmail") ?? "noreply@bhengu.local",
            payerPhone = request.Metadata?.GetValueOrDefault("payerPhone") ?? string.Empty,
            description = request.Description,
            responseurl = _options.CallbackUrl
        };

        var authHeader = $"remitaConsumerKey={_options.MerchantId},remitaConsumerToken={hash}";
        var body = await SendAsync(HttpMethod.Post, PaymentInitPath, requestBody, ct, "ProcessPayment", authHeader)
            .ConfigureAwait(false);

        var remitaResponse = JsonSerializer.Deserialize<RemitaPaymentInitResponse>(body);

        _logger.LogInformation("Remita payment init: orderId={OrderId} rrr={Rrr} statusCode={StatusCode}",
            orderId, remitaResponse?.Rrr, remitaResponse?.StatusCode);

        return new PaymentResponse
        {
            GatewayReference = remitaResponse?.Rrr ?? string.Empty,
            Status = MapStatusCode(remitaResponse?.StatusCode),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = remitaResponse?.Status
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var hash = Sha512Hex(_options.MerchantId + request.GatewayReference + amount + _options.ApiKey);

        var requestBody = new
        {
            merchantId = _options.MerchantId,
            rrr = request.GatewayReference,
            amount,
            reason = request.Reason,
            hash
        };

        var authHeader = $"remitaConsumerKey={_options.MerchantId},remitaConsumerToken={hash}";
        var body = await SendAsync(HttpMethod.Post, RefundPath, requestBody, ct, "ProcessRefund", authHeader)
            .ConfigureAwait(false);

        var refundResponse = JsonSerializer.Deserialize<RemitaRefundResponse>(body);

        _logger.LogInformation("Remita refund initiated: refundRef={RefundRef} rrr={Rrr}",
            refundResponse?.RefundReference, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.RefundReference ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatusCode(refundResponse?.StatusCode),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.FromBank) || string.IsNullOrWhiteSpace(_options.DebitAccount))
            throw new ProviderConfigurationException(ProviderName,
                "Remita payouts require FromBank and DebitAccount to be configured.");

        // DestinationToken format: "<creditBank>:<creditAccount>" (e.g. "058:0123456789").
        var colon = request.DestinationToken.IndexOf(':');
        if (colon <= 0)
            throw new BhenguPaymentException(ProviderName,
                "Remita PayoutRequest.DestinationToken must be 'creditBankCode:creditAccountNumber'.",
                providerErrorCode: "invalid_destination");

        var creditBank = request.DestinationToken[..colon];
        var creditAccount = request.DestinationToken[(colon + 1)..];
        var transRef = $"sm-{Guid.NewGuid():N}";
        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

        var hash = Sha512Hex(
            _options.MerchantId + creditAccount + amount + _options.ApiKey);

        var requestBody = new
        {
            fromBank = _options.FromBank,
            debitAccount = _options.DebitAccount,
            creditAccount,
            creditBank,
            narration = request.Description,
            amount,
            transRef,
            custName = request.Description
        };

        var authHeader = $"remitaConsumerKey={_options.MerchantId},remitaConsumerToken={hash}";
        var body = await SendAsync(HttpMethod.Post, SendMoneyPath, requestBody, ct, "ProcessPayout", authHeader)
            .ConfigureAwait(false);

        var payoutResponse = JsonSerializer.Deserialize<RemitaSendMoneyResponse>(body);

        _logger.LogInformation("Remita Single Send Money queued: transRef={TransRef} statusCode={StatusCode}",
            transRef, payoutResponse?.StatusCode);

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.TransRef ?? transRef,
            Status = MapStatusCode(payoutResponse?.StatusCode),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Verify a Remita webhook callback. Remita signs callbacks with SHA-512 of
    /// (rrr + status + apiKey). <paramref name="payload"/> is interpreted as the
    /// JSON callback body and parsed to extract rrr + status; <paramref name="signature"/> is
    /// the SHA-512 hex value Remita supplies.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("Remita ApiKey not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            var callback = JsonSerializer.Deserialize<RemitaWebhookEvent>(payload, s_caseInsensitive);
            if (callback is null || string.IsNullOrEmpty(callback.Rrr) || string.IsNullOrEmpty(callback.Status))
                return false;

            var expected = Sha512Hex(callback.Rrr + callback.Status + _options.ApiKey);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature),
                Encoding.UTF8.GetBytes(expected));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Remita webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var callback = JsonSerializer.Deserialize<RemitaWebhookEvent>(payload, s_caseInsensitive);
            if (callback is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Remita webhook: rrr={Rrr} status={Status}",
                callback.Rrr, callback.Status);

            var status = callback.Status?.ToLowerInvariant() switch
            {
                "00" or "01" or "success" or "successful" or "completed" => PaymentStatus.Completed,
                "021" or "025" or "pending" => PaymentStatus.Pending,
                "020" or "failed" or "declined" => PaymentStatus.Failed,
                "refunded" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (status is null || string.IsNullOrEmpty(callback.Rrr))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = callback.Rrr,
                Status = status.Value,
                EventType = callback.Status
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Remita webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation, string authHeader)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Remita failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Remita {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    private static PaymentStatus MapStatusCode(string? code) => code?.ToLowerInvariant() switch
    {
        "00" or "01" or "025" => PaymentStatus.Pending,            // Remita: 025=PaymentInitiated.
        "0" or "success" or "successful" or "completed" => PaymentStatus.Completed,
        "020" or "failed" or "declined" => PaymentStatus.Failed,
        "021" or "pending" => PaymentStatus.Pending,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Remita API response shapes (internal) ===

    private sealed class RemitaPaymentInitResponse
    {
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
        [JsonPropertyName("RRR")] public string? Rrr { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class RemitaRefundResponse
    {
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("refundReference")] public string? RefundReference { get; set; }
    }

    private sealed class RemitaSendMoneyResponse
    {
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("transRef")] public string? TransRef { get; set; }
    }

    private sealed class RemitaWebhookEvent
    {
        // Remita callbacks supply RRR — we use a case-insensitive deserializer in ParseWebhook/VerifySignature
        // to handle both "rrr" and "RRR" naming.
        [JsonPropertyName("rrr")] public string? Rrr { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
    }

    private static readonly JsonSerializerOptions s_caseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
