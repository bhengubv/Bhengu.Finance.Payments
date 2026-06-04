// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.AirtelMoney.Providers;

/// <summary>
/// Airtel Money provider. Implements Collect (charge), Disbursement (payout), and Refund.
/// Inbound callbacks are HMAC-SHA256 signed with <see cref="AirtelMoneyOptions.WebhookSecret"/>.
/// </summary>
public sealed class AirtelMoneyPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly AirtelMoneyOptions _options;
    private readonly AirtelMoneyOAuthCache _tokenCache;
    private readonly string _baseUrl;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.AirtelMoney;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney;

    /// <summary>Construct the provider with a distributed OAuth cache.</summary>
    public AirtelMoneyPaymentProvider(
        HttpClient httpClient,
        IOptions<AirtelMoneyOptions> options,
        ILogger<AirtelMoneyPaymentProvider> logger,
        IBhenguDistributedCache cache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(cache);
        _tokenCache = new AirtelMoneyOAuthCache(cache);

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.Country))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.Country)} is required");
        if (string.IsNullOrWhiteSpace(_options.Currency))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.Currency)} is required");

        _baseUrl = _options.BaseUrl ?? (_options.UseSandbox
            ? "https://openapiuat.airtel.africa/"
            : "https://openapi.airtel.africa/");
        if (!_baseUrl.EndsWith('/')) _baseUrl += "/";

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public AirtelMoneyPaymentProvider(
        HttpClient httpClient,
        IOptions<AirtelMoneyOptions> options,
        ILogger<AirtelMoneyPaymentProvider> logger)
        : this(httpClient, options, logger, new InMemoryBhenguDistributedCache())
    {
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var msisdn = request.PaymentMethodToken;
        if (string.IsNullOrWhiteSpace(msisdn))
            throw new PaymentDeclinedException(ProviderName, "missing_msisdn",
                "Airtel Money requires the payer MSISDN in PaymentRequest.PaymentMethodToken.");

        var transactionId = request.Metadata?.TryGetValue("transaction_id", out var tx) == true
            ? tx
            : Guid.NewGuid().ToString("N")[..16];
        var reference = request.Metadata?.TryGetValue("reference", out var r) == true
            ? r
            : request.Description;

        var body = new
        {
            reference,
            subscriber = new { country = _options.Country, currency = _options.Currency, msisdn },
            transaction = new
            {
                amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
                country = _options.Country,
                currency = _options.Currency,
                id = transactionId
            }
        };

        var (responseBody, _) = await SendAsync(
            HttpMethod.Post, "merchant/v1/payments/", body, ct, "Collect").ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<AirtelEnvelope<AirtelCollectData>>(responseBody);
        var data = result?.Data;
        var status = MapStatus(data?.Transaction?.Status);

        Logger.LogInformation(
            "Airtel Money Collect accepted: TransactionId={TransactionId} AirtelMoneyId={AirtelMoneyId} Status={Status}",
            transactionId, data?.Transaction?.AirtelMoneyId, data?.Transaction?.Status);

        return new PaymentResponse
        {
            GatewayReference = data?.Transaction?.Id ?? transactionId,
            Status = status,
            Amount = request.Amount,
            Currency = _options.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = result?.Status?.Message
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var body = new { transaction = new { airtel_money_id = request.GatewayReference } };

        var (responseBody, _) = await SendAsync(
            HttpMethod.Post, "standard/v1/payments/refund", body, ct, "Refund").ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<AirtelEnvelope<AirtelRefundData>>(responseBody);
        var status = MapStatus(result?.Data?.Transaction?.Status);

        Logger.LogInformation(
            "Airtel Money Refund accepted: AirtelMoneyId={AirtelMoneyId} Status={Status}",
            request.GatewayReference, result?.Data?.Transaction?.Status);

        return new RefundResponse
        {
            GatewayReference = result?.Data?.Transaction?.AirtelMoneyId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = status,
            ProcessedAt = DateTime.UtcNow,
            Message = result?.Status?.Message
        };
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationToken))
            throw new PaymentDeclinedException(ProviderName, "invalid_msisdn",
                "Airtel Money Disbursement requires the recipient MSISDN in PayoutRequest.DestinationToken.");

        var transactionId = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? request.IdempotencyKey!
            : Guid.NewGuid().ToString("N")[..16];

        var body = new
        {
            payee = new { msisdn = request.DestinationToken },
            reference = transactionId,
            pin = _options.EncryptedDisbursementPin ?? string.Empty,
            transaction = new
            {
                amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
                id = transactionId
            }
        };

        var (responseBody, _) = await SendAsync(
            HttpMethod.Post, "standard/v1/disbursements/", body, ct, "Disbursement").ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<AirtelEnvelope<AirtelDisbursementData>>(responseBody);
        var status = MapStatus(result?.Data?.Transaction?.Status);

        Logger.LogInformation(
            "Airtel Money Disbursement accepted: TransactionId={TransactionId} Status={Status}",
            transactionId, result?.Data?.Transaction?.Status);

        return new PayoutResponse
        {
            GatewayReference = result?.Data?.Transaction?.Id ?? transactionId,
            Status = status,
            Amount = request.Amount,
            Currency = _options.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
            {
                Logger.LogWarning("Airtel Money WebhookSecret not configured — signature verification cannot succeed.");
                return false;
            }

            // Airtel returns the HMAC-SHA256 digest Base64-encoded on the webhook header.
            return SignatureHelpers.VerifyHmacSha256(payload, signature, _options.WebhookSecret, SignatureHelpers.Encoding.Base64);
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("webhook", () => ParseWebhookCoreAsync(payload, ct), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<AirtelWebhookPayload>(payload);
            var txn = evt?.Transaction;
            if (txn is null || string.IsNullOrEmpty(txn.Id))
                return Task.FromResult<WebhookEvent?>(null);

            var status = MapStatus(txn.StatusCode);
            var eventType = (evt!.EventType ?? txn.StatusCode ?? "unknown").ToLowerInvariant();
            var gatewayReference = txn.AirtelMoneyId ?? txn.Id;

            Logger.LogInformation(
                "Parsed Airtel Money webhook: TransactionId={TransactionId} Status={Status} EventType={EventType}",
                txn.Id, txn.StatusCode, eventType);

            // Surface disbursement / payout events as typed records so consumers can switch on the concrete type.
            // Airtel webhook event_type strings include "disbursement", "payout" or "transfer" for outbound transactions.
            var isPayout = eventType.Contains("disburs", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("payout", StringComparison.OrdinalIgnoreCase)
                || eventType.Contains("transfer", StringComparison.OrdinalIgnoreCase);

            if (isPayout)
            {
                if (status == PaymentStatus.Completed)
                {
                    return Task.FromResult<WebhookEvent?>(new PayoutCompletedEvent
                    {
                        GatewayReference = gatewayReference,
                        PayoutReference = gatewayReference,
                        Status = status,
                        EventType = eventType,
                        Category = WebhookEventCategory.PayoutCompleted,
                        Amount = 0m,
                        Currency = _options.Currency
                    });
                }

                if (status == PaymentStatus.Failed)
                {
                    return Task.FromResult<WebhookEvent?>(new PayoutFailedEvent
                    {
                        GatewayReference = gatewayReference,
                        PayoutReference = gatewayReference,
                        Status = status,
                        EventType = eventType,
                        Category = WebhookEventCategory.PayoutFailed,
                        Amount = 0m,
                        Currency = _options.Currency,
                        FailureCode = txn.StatusCode,
                        FailureMessage = txn.Message
                    });
                }
            }

            var category = status switch
            {
                PaymentStatus.Completed => WebhookEventCategory.ChargeSucceeded,
                PaymentStatus.Pending => WebhookEventCategory.ChargePending,
                PaymentStatus.Failed => WebhookEventCategory.ChargeFailed,
                _ => WebhookEventCategory.Unknown
            };

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = gatewayReference,
                Status = status,
                EventType = eventType,
                Category = category
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Airtel Money webhook payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // ===== HTTP plumbing =====

    private async Task<(string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Country", _options.Country);
        req.Headers.Add("X-Currency", _options.Currency);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Airtel Money {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return (responseBody, response);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var cached = await _tokenCache.GetAsync(_options.ClientId, ct).ConfigureAwait(false);
        if (cached is not null) return cached;

        await _tokenCache.WaitFetchSlotAsync(ct).ConfigureAwait(false);
        try
        {
            cached = await _tokenCache.GetAsync(_options.ClientId, ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var body = new
            {
                client_id = _options.ClientId,
                client_secret = _options.ClientSecret,
                grant_type = "client_credentials"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "auth/oauth2/token")
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogError("Airtel Money OAuth failed: {StatusCode} {Body}", response.StatusCode, responseBody);
                throw new ProviderUnavailableException(ProviderName, $"Airtel Money OAuth HTTP {(int)response.StatusCode}: {responseBody}");
            }

            var token = JsonSerializer.Deserialize<AirtelOAuthResponse>(responseBody);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "Airtel Money OAuth returned an empty token");

            var ttl = TimeSpan.FromSeconds(Math.Max(60, (token.ExpiresIn > 0 ? token.ExpiresIn : 3599) - 60));
            await _tokenCache.SetAsync(_options.ClientId, token.AccessToken, ttl, ct).ConfigureAwait(false);
            return token.AccessToken;
        }
        finally
        {
            _tokenCache.ReleaseFetchSlot();
        }
    }

    private static PaymentStatus MapStatus(string? raw) => (raw ?? string.Empty).ToUpperInvariant() switch
    {
        "TS" or "SUCCESS" or "SUCCESSFUL" or "COMPLETED" => PaymentStatus.Completed,
        "TIP" or "PENDING" or "IN_PROGRESS" => PaymentStatus.Pending,
        "TF" or "FAILED" or "DECLINED" => PaymentStatus.Failed,
        "TA" or "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // ===== Airtel JSON shapes (internal) =====

    private sealed class AirtelOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class AirtelEnvelope<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
        [JsonPropertyName("status")] public AirtelStatus? Status { get; set; }
    }

    private sealed class AirtelStatus
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; }
    }

    private sealed class AirtelCollectData
    {
        [JsonPropertyName("transaction")] public AirtelTransaction? Transaction { get; set; }
    }

    private sealed class AirtelRefundData
    {
        [JsonPropertyName("transaction")] public AirtelTransaction? Transaction { get; set; }
    }

    private sealed class AirtelDisbursementData
    {
        [JsonPropertyName("transaction")] public AirtelTransaction? Transaction { get; set; }
    }

    private sealed class AirtelTransaction
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("airtel_money_id")] public string? AirtelMoneyId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class AirtelWebhookPayload
    {
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("transaction")] public AirtelWebhookTransaction? Transaction { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
    }

    private sealed class AirtelWebhookTransaction
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("airtel_money_id")] public string? AirtelMoneyId { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("status_code")] public string? StatusCode { get; set; }
    }
}
