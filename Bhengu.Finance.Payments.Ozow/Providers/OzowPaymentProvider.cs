// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Ozow.Providers;

/// <summary>
/// Ozow payment gateway provider — instant EFT and PayShap payments for South Africa.
/// Ozow's standard merchant API does NOT expose payouts — <see cref="IPayoutProvider"/>
/// is intentionally not implemented; merchants requiring disbursements should use Ozow's
/// separate Disbursement API.
/// </summary>
public sealed class OzowPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly OzowOptions _options;
    private readonly OzowIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Ozow;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public OzowPaymentProvider(
        HttpClient httpClient,
        IOptions<OzowOptions> options,
        ILogger<OzowPaymentProvider> logger,
        OzowIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.SiteCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OzowOptions.SiteCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.PrivateKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OzowOptions.PrivateKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OzowOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://api-sandbox.ozow.com/")
                : (_options.BaseUrl ?? "https://api.ozow.com/"));
        }

        if (!_httpClient.DefaultRequestHeaders.Contains("ApiKey"))
            _httpClient.DefaultRequestHeaders.Add("ApiKey", _options.ApiKey);
        if (!_httpClient.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/json"))
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Currency);
        var sw = Stopwatch.StartNew();

        var transactionReference = request.Metadata?.GetValueOrDefault("transaction_reference")
            ?? Guid.NewGuid().ToString("N");
        var amountString = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var currency = request.Currency.ToUpperInvariant();

        var hashInput = string.Concat(_options.SiteCode, transactionReference, amountString, currency, _options.PrivateKey);
        var hashCheck = GenerateSha512Hash(hashInput);

        var requestBody = new
        {
            siteCode = _options.SiteCode,
            transactionReference,
            amount = amountString,
            currency,
            bankReference = request.Description,
            cancelUrl = request.Metadata?.GetValueOrDefault("cancel_url") ?? string.Empty,
            errorUrl = request.Metadata?.GetValueOrDefault("error_url") ?? string.Empty,
            successUrl = request.Metadata?.GetValueOrDefault("success_url") ?? string.Empty,
            notifyUrl = request.Metadata?.GetValueOrDefault("notify_url") ?? string.Empty,
            isTest = _options.UseSandbox,
            hashCheck,
            customer = new
            {
                firstName = request.Metadata?.GetValueOrDefault("customer_first_name") ?? string.Empty,
                lastName = request.Metadata?.GetValueOrDefault("customer_last_name") ?? string.Empty,
                email = request.Metadata?.GetValueOrDefault("customer_email") ?? string.Empty,
                phone = request.Metadata?.GetValueOrDefault("customer_phone") ?? string.Empty
            }
        };

        if (!string.IsNullOrEmpty(request.PaymentMethodToken))
            Logger.LogDebug("Ozow ProcessPayment called with PaymentMethodToken={Token} (not used by redirect flow)", request.PaymentMethodToken);

        try
        {
            var body = await SendAsync(HttpMethod.Post, "postpaymentrequest", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
            var ozowResponse = JsonSerializer.Deserialize<OzowPaymentApiResponse>(body, DeserializeOptions);

            Logger.LogInformation("Ozow payment request created: {TransactionId} status={Status}",
                ozowResponse?.TransactionId, ozowResponse?.Status);

            var status = MapStatus(ozowResponse?.Status ?? "pending");
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", status.ToString().ToLowerInvariant()));

            return new PaymentResponse
            {
                GatewayReference = ozowResponse?.TransactionId ?? transactionReference,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = ozowResponse?.PaymentUrl is { Length: > 0 } url ? url : null,
                Message = "Payment initiated"
            };
        }
        catch (Exception)
        {
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", BhenguPaymentDiagnostics.Outcomes.Error));
            throw;
        }
        finally
        {
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderName));
        }
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderName, request.GatewayReference);
        var requestBody = new
        {
            siteCode = _options.SiteCode,
            transactionId = request.GatewayReference,
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            reason = request.Reason
        };

        var body = await SendAsync(HttpMethod.Post, "refund", requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<OzowRefundResponse>(body, DeserializeOptions);

        Logger.LogInformation("Ozow refund created: {RefundId} for transaction {TransactionId}",
            refundResponse?.RefundId, request.GatewayReference);

        var status = MapStatus(refundResponse?.Status ?? "pending");
        BhenguPaymentDiagnostics.RefundsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("outcome", status == PaymentStatus.Refunded ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending));

        return new RefundResponse
        {
            GatewayReference = refundResponse?.RefundId ?? string.Empty,
            Amount = request.Amount,
            Status = status,
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        bool valid;
        if (string.IsNullOrWhiteSpace(_options.PrivateKey))
        {
            Logger.LogWarning("Ozow PrivateKey not configured — signature verification cannot succeed.");
            valid = false;
        }
        else
        {
            try
            {
                var hashInput = payload + _options.PrivateKey;
                var computedHash = GenerateSha512Hash(hashInput);

                valid = CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                    Encoding.UTF8.GetBytes(computedHash));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Ozow webhook signature verification raised");
                valid = false;
            }
        }

        BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderName),
            new KeyValuePair<string, object?>("valid", valid));
        return valid;
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderName);
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<OzowWebhookNotification>(payload, DeserializeOptions);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Ozow webhook event: TransactionId={TransactionId} status={Status}",
                webhookEvent.TransactionId, webhookEvent.Status);

            var reference = !string.IsNullOrEmpty(webhookEvent.TransactionReference)
                ? webhookEvent.TransactionReference
                : webhookEvent.TransactionId;
            if (string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            var status = MapStatus(webhookEvent.Status ?? "");
            var amount = webhookEvent.Amount ?? 0m;
            var currency = "ZAR";

            return Task.FromResult<WebhookEvent?>(status switch
            {
                PaymentStatus.Completed => new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = status,
                    EventType = "ozow.notification",
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency
                },
                PaymentStatus.Pending => new ChargePendingEvent
                {
                    GatewayReference = reference,
                    Status = status,
                    EventType = "ozow.notification",
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                },
                PaymentStatus.Failed => new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = status,
                    EventType = "ozow.notification",
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = webhookEvent.Status,
                    FailureMessage = webhookEvent.StatusMessage
                },
                PaymentStatus.Refunded => new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = status,
                    EventType = "ozow.notification",
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                },
                _ => new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = status,
                    EventType = "ozow.notification",
                    Category = WebhookEventCategory.Unknown
                }
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Ozow webhook event");
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Ozow failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Ozow timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Ozow {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static string GenerateSha512Hash(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "complete" or "completed" => PaymentStatus.Completed,
        "pending" or "pendinginvestigation" => PaymentStatus.Pending,
        "cancelled" or "canceled" or "abandoned" => PaymentStatus.Cancelled,
        "error" or "failed" => PaymentStatus.Failed,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Ozow API response shapes (internal) ===

    private sealed class OzowPaymentApiResponse
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("paymentUrl")] public string? PaymentUrl { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class OzowRefundResponse
    {
        [JsonPropertyName("refundId")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class OzowWebhookNotification
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("transactionReference")] public string? TransactionReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("statusMessage")] public string? StatusMessage { get; set; }
        [JsonPropertyName("hash")] public string? Hash { get; set; }
    }
}
