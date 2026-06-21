// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Remita.Providers;

/// <summary>
/// Remita (SystemSpecs) e-collection provider. Wraps Remita's documented RRR (Remita Retrieval
/// Reference) generation flow for Nigerian revenue collection. Authentication uses SHA-512 hex
/// hashes of concatenated fields with the configured API key carried in the
/// <c>remitaConsumerKey=..,remitaConsumerToken=..</c> Authorization header — Remita's e-collection
/// endpoints do NOT use bearer tokens.
/// </summary>
/// <remarks>
/// <para><b>Scope.</b> This provider implements Remita's publicly documented <c>paymentinit</c>
/// (RRR generation) endpoint. The verified wire format is taken from Remita's own sample SDK
/// (<see href="https://github.com/RemitaPaymentServices/remita-rrr-generator-status-dotnet"/>).</para>
/// <para><b>Refund / payout are deliberately not implemented.</b> Remita publishes no e-collection
/// refund API (a code search of every official <c>RemitaPaymentServices</c> SDK returns no refund
/// endpoint), and its real disbursement product (RITS — <c>rpgsvc/v3/rpg/single/payment</c>) is a
/// separate Bearer-token + AES-128-CBC integration that shares neither host path, auth scheme, nor
/// request shape with this e-collection surface. Rather than ship invented wire details for money
/// movement, <see cref="ProcessRefundAsync"/> and <see cref="ProcessPayoutAsync"/> throw
/// <c>not_supported</c>. See <see href="https://github.com/RemitaPaymentServices/rits-sdk-dotnet-v3"/>
/// to integrate RITS as a dedicated provider when required.</para>
/// </remarks>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "RRR-generation wire format built from Remita's published sample SDK (github.com/RemitaPaymentServices/remita-rrr-generator-status-dotnet); never sandbox-verified.")]
public sealed class RemitaPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    // Verified against Remita's official sample (RemitaPaymentServices/remita-rrr-generator-status-dotnet,
    // Program.cs GENERATE_RRR) and the PHP sample (remita-rrr-generator-status-php). Demo host
    // https://remitademo.net, live host https://login.remita.net.
    private const string PaymentInitPath =
        "remita/exapp/api/v1/send/api/echannelsvc/merchant/api/paymentinit";

    private readonly HttpClient _httpClient;
    private readonly RemitaOptions _options;
    private readonly RemitaIdempotencyCache? _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Remita;

    /// <inheritdoc />
    /// <remarks>
    /// Refund / PartialRefund / Payout are intentionally absent: Remita exposes no public
    /// e-collection refund or send-money endpoint on this surface (see <see cref="ProcessRefundAsync"/>
    /// / <see cref="ProcessPayoutAsync"/>, which throw <c>not_supported</c>).
    /// </remarks>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Settlement |
        ProviderCapabilities.Mandates |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public RemitaPaymentProvider(
        HttpClient httpClient,
        IOptions<RemitaOptions> options,
        ILogger<RemitaPaymentProvider> logger,
        RemitaIdempotencyCache? idempotency = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency;

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

    /// <inheritdoc />
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? ProcessPaymentCoreAsync(request, ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "charge",
                () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
        => RunChargeAsync(request.Currency, async () =>
        {
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

            Logger.LogInformation("Remita payment init: orderId={OrderId} rrr={Rrr} statusCode={StatusCode}",
                orderId, remitaResponse?.Rrr, remitaResponse?.StatusCode);

            var status = MapStatusCode(remitaResponse?.StatusCode);

            return new PaymentResponse
            {
                GatewayReference = remitaResponse?.Rrr ?? string.Empty,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = remitaResponse?.Status
            };
        }, ct);

    /// <inheritdoc />
    /// <remarks>
    /// Remita exposes no public refund API on its e-collection surface. A code search of every
    /// official <c>RemitaPaymentServices</c> SDK (RRR/status, direct-debit mandate, and RITS
    /// disbursement) returns no refund endpoint. Refunds/reversals are handled out-of-band through
    /// the Remita merchant portal, so this method throws rather than POST an invented path.
    /// </remarks>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(ProviderName,
            "Remita exposes no public refund API; process refunds/reversals via the Remita merchant portal.",
            providerErrorCode: "not_supported");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Remita's e-collection surface (the one this provider speaks) has no send-money endpoint. Its
    /// real disbursement product is RITS (<c>rpgsvc/v3/rpg/single/payment</c>), a separate
    /// Bearer-token + AES-128-CBC API with a different host path, auth model and request shape —
    /// see <see href="https://github.com/RemitaPaymentServices/rits-sdk-dotnet-v3"/>. Rather than
    /// emit invented wire details for money movement, this method throws; integrate RITS as a
    /// dedicated provider to disburse.
    /// </remarks>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(ProviderName,
            "Remita exposes no public disbursement API on this e-collection surface; use the Remita RITS API (rits-sdk-dotnet) as a dedicated provider to send money.",
            providerErrorCode: "not_supported");
    }

    /// <summary>
    /// Verify a Remita webhook callback. <paramref name="payload"/> is interpreted as the JSON
    /// callback body and parsed to extract rrr + status; <paramref name="signature"/> is the
    /// SHA-512 hex value Remita supplies. NOTE: the callback-signature scheme is UNVERIFIED —
    /// Remita publishes no callback-signature spec in its official sample SDKs (see the inline note
    /// on the hash construction). Do not rely on this as the sole authenticity gate without
    /// confirming against a real Remita merchant notification.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_options.ApiKey))
                {
                    Logger.LogWarning("Remita ApiKey not configured — signature verification cannot succeed.");
                    return false;
                }

                var callback = JsonSerializer.Deserialize<RemitaWebhookEvent>(payload, s_caseInsensitive);
                if (callback is null || string.IsNullOrEmpty(callback.Rrr) || string.IsNullOrEmpty(callback.Status))
                    return false;

                // UNVERIFIED: Remita publishes no callback-signature spec in its official sample SDKs.
                // The SHA512(rrr+status+apiKey) scheme here is a best-effort guess; confirm against a
                // real Remita merchant notification before trusting it as the sole authenticity gate.
                var expected = Sha512Hex(callback.Rrr + callback.Status + _options.ApiKey);
                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature),
                    Encoding.UTF8.GetBytes(expected));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Remita webhook signature verification raised");
                return false;
            }
        });
    }

    /// <inheritdoc />
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () =>
        {
            try
            {
                var callback = JsonSerializer.Deserialize<RemitaWebhookEvent>(payload, s_caseInsensitive);
                if (callback is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Remita webhook: rrr={Rrr} status={Status} type={Type}",
                    callback.Rrr, callback.Status, callback.NotificationType);

                return Task.FromResult(MapWebhookEvent(callback));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Remita webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(RemitaWebhookEvent callback)
    {
        var rrr = callback.Rrr ?? callback.OrderId ?? string.Empty;
        if (string.IsNullOrEmpty(rrr)) return null;

        var rawStatus = callback.Status?.ToLowerInvariant();
        var notificationType = callback.NotificationType?.ToLowerInvariant();
        var amount = callback.Amount;
        var currency = callback.Currency ?? "NGN";

        // Mandate notifications carry an explicit notificationType prefix.
        if (notificationType is not null)
        {
            switch (notificationType)
            {
                case "mandate.activated":
                case "mandate.active":
                    return new MandateActivatedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Completed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.MandateActivated,
                        MandateReference = callback.MandateId ?? rrr,
                        AmountLimit = amount > 0 ? amount : null,
                        Currency = currency
                    };

                case "mandate.cancelled":
                case "mandate.canceled":
                case "mandate.terminated":
                    return new MandateCancelledEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Cancelled,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.MandateCancelled,
                        MandateReference = callback.MandateId ?? rrr,
                        CancellationReason = callback.CancellationReason
                    };

                case "payout.successful":
                case "transfer.successful":
                    return new PayoutCompletedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Completed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = callback.TransRef ?? rrr,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = callback.CreditAccount
                    };

                case "payout.failed":
                case "transfer.failed":
                    return new PayoutFailedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Failed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = callback.TransRef ?? rrr,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = callback.Status,
                        FailureMessage = callback.Message
                    };

                case "settlement.completed":
                    return new SettlementCompletedEvent
                    {
                        GatewayReference = rrr,
                        Status = PaymentStatus.Completed,
                        EventType = callback.NotificationType,
                        Category = WebhookEventCategory.SettlementCompleted,
                        SettlementReference = rrr,
                        NetAmount = amount,
                        Currency = currency
                    };
            }
        }

        // Otherwise fall through to status-based mapping (Remita's classic e-collection callback).
        switch (rawStatus)
        {
            case "00":
            case "01":
            case "success":
            case "successful":
            case "completed":
                return new ChargeSucceededEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Completed,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency
                };

            case "021":
            case "025":
            case "pending":
                return new ChargePendingEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Pending,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                };

            case "020":
            case "failed":
            case "declined":
                return new ChargeFailedEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Failed,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = callback.Status,
                    FailureMessage = callback.Message
                };

            case "refunded":
                return new RefundSucceededEvent
                {
                    GatewayReference = rrr,
                    Status = PaymentStatus.Refunded,
                    EventType = callback.Status,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = rrr,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            default:
                return null;
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
            Logger.LogError("Remita {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
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

    private sealed class RemitaWebhookEvent
    {
        [JsonPropertyName("rrr")] public string? Rrr { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("notificationType")] public string? NotificationType { get; set; }
        [JsonPropertyName("mandateId")] public string? MandateId { get; set; }
        [JsonPropertyName("transRef")] public string? TransRef { get; set; }
        [JsonPropertyName("creditAccount")] public string? CreditAccount { get; set; }
        [JsonPropertyName("cancellationReason")] public string? CancellationReason { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private static readonly JsonSerializerOptions s_caseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
