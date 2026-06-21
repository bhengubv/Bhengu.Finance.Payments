// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Kashier.Providers;

/// <summary>
/// Kashier (Egypt) payment gateway provider. Wraps Kashier's server REST API and the
/// hosted-payment-page (HPP) order-hash protocol.
/// </summary>
/// <remarks>
/// Wire format verified against Kashier's published documentation and reference SDKs:
/// <list type="bullet">
///   <item>Server charge — <c>POST {REST}/checkout</c>. Source: www.kashier.io/docs/integration-guide and the
///         asciisd/kashier SDK (<c>URL_PATH_CHECKOUT = '/checkout'</c>, <c>Checkout.php</c>).</item>
///   <item>Refund — <c>PUT {REST}/orders/{orderId}/transactions/{transactionId}?operation=refund</c>.
///         Source: asciisd/kashier <c>Refund.php</c>; corroborated by developers.kashier.io/payment/refund.</item>
///   <item>Order reconciliation / status — <c>GET {REST}/payments/orders/{merchantOrderId}</c>.
///         Source: developers.kashier.io/payment/orderreconciliation.</item>
///   <item>Order hash — HMAC-SHA256 hex over <c>/?payment={mid}.{orderId}.{amount}.{currency}</c>. Verified
///         against the integration-guide test vector (mid-0-1.99.20.EGP / secret 11111 →
///         606a8a1307d64caf4e2e9bb724738f115a8972c27eccb2a8acd9194c357e4bec).</item>
///   <item>Webhook signature — see <see cref="VerifyWebhookSignature"/>.</item>
/// </list>
/// Kashier does <b>not</b> publicly document a server-side payout/disbursement API, so this provider does not
/// implement <c>IPayoutProvider</c> — shipping a guessed payout path would be worse than honestly not
/// supporting it.
/// </remarks>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from Kashier public docs + reference SDKs (kashier.io/docs/integration-guide, developers.kashier.io, asciisd/kashier); never sandbox-verified.")]
public sealed class KashierPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;
    private readonly KashierIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Kashier;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Tokenisation |
        ProviderCapabilities.ThreeDSecure |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public KashierPaymentProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierPaymentProvider> logger,
        KashierIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.MerchantId)} is required");

        KashierHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
        => RunChargeAsync(request.Currency, async () =>
        {
            var orderId = request.Metadata?.GetValueOrDefault("orderId") ?? $"kashier-{Guid.NewGuid():N}";
            var amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            var currency = string.IsNullOrWhiteSpace(request.Currency) ? _options.Currency : request.Currency.ToUpperInvariant();
            var hash = ComputeOrderHash(_options.MerchantId, orderId, amount, currency, HashKey);

            // POST /checkout body — field names per the Kashier integration guide and the asciisd CheckoutRequest.
            var requestBody = new Dictionary<string, object?>
            {
                ["merchantId"] = _options.MerchantId,
                ["orderId"] = orderId,
                ["amount"] = amount,
                ["currency"] = currency,
                ["hash"] = hash,
                ["shopper_reference"] = request.Metadata?.GetValueOrDefault("shopperReference") ?? request.CustomerId,
                // A previously-vaulted Kashier card token, when paying with a saved card.
                ["cardToken"] = request.PaymentMethodToken,
                ["merchantRedirect"] = _options.RedirectUrl,
                ["display"] = "en"
            };

            var body = await KashierHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "checkout",
                requestBody, "ProcessPayment", ct, request.IdempotencyKey).ConfigureAwait(false);
            var kashierResponse = JsonSerializer.Deserialize<KashierCheckoutResponse>(body, KashierHttpClient.Json);
            var data = kashierResponse?.Response;

            var txId = data?.TransactionId ?? data?.KashierOrderId ?? data?.MerchantOrderId ?? orderId;
            // The envelope "status" only signals API-call success, not the authorization outcome — a declined
            // card can still come back under a SUCCESS envelope. Prefer the card result / transaction status so
            // declines aren't masked; fall back to the envelope only when neither is present.
            var status = MapStatus(data?.Card?.Result ?? data?.Status ?? kashierResponse?.Status);

            Logger.LogInformation("Kashier charge created: order={OrderId} tx={Tx} status={Status} 3ds={ThreeDs}",
                orderId, txId, status, data?.Card?.ThreeDSecure);

            return new PaymentResponse
            {
                GatewayReference = txId,
                Status = status,
                Amount = request.Amount,
                Currency = currency,
                ProcessedAt = DateTime.UtcNow,
                // Kashier returns a 3DS/ACS redirect URL when an issuer step-up is required.
                RedirectUrl = data?.RedirectUrl ?? data?.Card?.AcsUrl,
                Message = kashierResponse?.Messages?.En ?? data?.TransactionResponseCode ?? data?.Status
            };
        }, ct);

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
        => RunRefundAsync(request.GatewayReference, async () =>
        {
            // Kashier's refund path requires both the order id and the transaction id:
            //   PUT /orders/{orderId}/transactions/{transactionId}?operation=refund
            // The SDK exposes a single GatewayReference. We persist the Kashier order id as the charge's
            // GatewayReference, and Kashier accepts the order id in the transaction slot for the refund of that
            // order's primary transaction, so we use the reference for both segments. Callers needing to refund a
            // specific non-primary transaction should pass a "{orderId}:{transactionId}" reference, which we split.
            var reference = request.GatewayReference;
            string orderId, transactionId;
            var sep = reference.IndexOf(':');
            if (sep > 0 && sep < reference.Length - 1)
            {
                orderId = reference[..sep];
                transactionId = reference[(sep + 1)..];
            }
            else
            {
                orderId = transactionId = reference;
            }

            var path = $"orders/{Uri.EscapeDataString(orderId)}/transactions/{Uri.EscapeDataString(transactionId)}?operation=refund";
            var requestBody = new
            {
                merchantId = _options.MerchantId,
                orderId,
                transactionId,
                amount = request.Amount.ToString("0.00", CultureInfo.InvariantCulture)
            };

            var body = await KashierHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Put, path, requestBody, "ProcessRefund", ct, request.IdempotencyKey).ConfigureAwait(false);
            var refundResponse = JsonSerializer.Deserialize<KashierCheckoutResponse>(body, KashierHttpClient.Json);
            var data = refundResponse?.Response;

            // Prefer the transaction status over the envelope status (envelope = API-call success only).
            var rawStatus = data?.Status ?? refundResponse?.Status;
            Logger.LogInformation("Kashier refund created: tx={Tx} status={Status}", transactionId, rawStatus);

            var mapped = MapStatus(rawStatus);
            var outcome = mapped is PaymentStatus.Completed or PaymentStatus.Refunded ? PaymentStatus.Refunded : mapped;

            return new RefundResponse
            {
                GatewayReference = data?.TransactionId ?? transactionId,
                Amount = request.Amount,
                Status = outcome,
                ProcessedAt = DateTime.UtcNow,
                Message = refundResponse?.Messages?.En ?? data?.Status
            };
        }, ct);

    /// <summary>
    /// Retrieve an order's reconciliation record from Kashier (<c>GET /payments/orders/{merchantOrderId}</c>).
    /// Returns the mapped <see cref="PaymentStatus"/>, or <see cref="PaymentStatus.Pending"/> when Kashier has
    /// no record yet.
    /// </summary>
    public Task<PaymentResponse> GetOrderStatusAsync(string merchantOrderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(merchantOrderId);
        return RunOperationAsync("get_order_status", async () =>
        {
            var body = await KashierHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, $"payments/orders/{Uri.EscapeDataString(merchantOrderId)}",
                null, "GetOrderStatus", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<KashierCheckoutResponse>(body, KashierHttpClient.Json);
            var data = response?.Response;

            var amount = decimal.TryParse(data?.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ? amt : 0m;
            return new PaymentResponse
            {
                GatewayReference = data?.TransactionId ?? data?.KashierOrderId ?? merchantOrderId,
                Status = MapStatus(data?.Status ?? response?.Status),
                Amount = amount,
                Currency = data?.Currency ?? _options.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.Messages?.En ?? data?.Status
            };
        }, ct);
    }

    /// <summary>
    /// Verify an inbound Kashier webhook signature (the <c>x-kashier-signature</c> header).
    /// </summary>
    /// <remarks>
    /// Per developers.kashier.io/payment/webhook and the asciisd/kashier <c>KashierResponseSignature</c>:
    /// the webhook body's <c>data</c> object carries a <c>signatureKeys</c> array naming which of its fields
    /// are signed. The canonical string is those fields, <b>sorted alphabetically by key</b>, joined as an
    /// RFC-3986-encoded query string (<c>k=v&amp;k=v</c>), then HMAC-SHA256 (hex) keyed by the <b>Payment API
    /// Key</b>. The result is compared in constant time to the supplied signature.
    /// </remarks>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            var key = WebhookKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                Logger.LogWarning("Kashier webhook signing key not configured — signature verification cannot succeed.");
                return false;
            }

            try
            {
                var canonical = BuildWebhookSignaturePayload(payload);
                if (canonical is null)
                {
                    Logger.LogWarning("Kashier webhook payload missing 'data'/'signatureKeys' — cannot verify signature.");
                    return false;
                }

                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
                var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

                return CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                    Encoding.UTF8.GetBytes(computed));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Kashier webhook signature verification raised");
                return false;
            }
        });
    }

    /// <summary>
    /// Build the canonical signed string from a Kashier webhook body: take <c>data.signatureKeys</c>, sort the
    /// keys alphabetically, and emit an RFC-3986 query string of <c>key=value</c> pairs read from <c>data</c>.
    /// Returns null when the payload is not a signable Kashier webhook.
    /// </summary>
    public static string? BuildWebhookSignaturePayload(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
            return null;
        if (!dataEl.TryGetProperty("signatureKeys", out var keysEl) || keysEl.ValueKind != JsonValueKind.Array)
            return null;

        var keys = keysEl.EnumerateArray()
            .Where(k => k.ValueKind == JsonValueKind.String)
            .Select(k => k.GetString()!)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        var pairs = new List<string>(keys.Count);
        foreach (var k in keys)
        {
            if (!dataEl.TryGetProperty(k, out var valEl)) continue;
            var raw = valEl.ValueKind switch
            {
                JsonValueKind.String => valEl.GetString() ?? string.Empty,
                JsonValueKind.Null => string.Empty,
                _ => valEl.GetRawText()
            };
            // RFC 3986 encoding to match Kashier's http_build_query(..., PHP_QUERY_RFC3986).
            pairs.Add($"{Rfc3986Escape(k)}={Rfc3986Escape(raw)}");
        }

        return string.Join("&", pairs);
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () =>
        {
            try
            {
                var webhook = JsonSerializer.Deserialize<KashierWebhookEvent>(payload, KashierHttpClient.Json);
                if (webhook is null) return Task.FromResult<WebhookEvent?>(null);

                var data = webhook.Data;
                // Kashier keys the order on merchantOrderId / kashierOrderId; the financial id is transactionId.
                var reference = data?.TransactionId ?? data?.MerchantOrderId ?? data?.KashierOrderId;
                if (string.IsNullOrEmpty(reference))
                    return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Kashier webhook: event={Event} status={Status}", webhook.Event, data?.Status);

                var amount = decimal.TryParse(data?.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ? amt : 0m;
                var currency = data?.Currency ?? _options.Currency;
                var eventUpper = webhook.Event?.ToUpperInvariant();
                var statusUpper = data?.Status?.ToUpperInvariant();

                return Task.FromResult<WebhookEvent?>(eventUpper switch
                {
                    // Kashier emits "pay" for a payment event and "refund" for a refund event; the outcome is on data.status.
                    "PAY" or "CAPTURE" when statusUpper is "SUCCESS" or "PAID" or "APPROVED" => new ChargeSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = webhook.Event,
                        Category = WebhookEventCategory.ChargeSucceeded,
                        Amount = amount,
                        Currency = currency,
                        CustomerId = data?.ShopperReference
                    },
                    "PAY" or "CAPTURE" when statusUpper is "FAILED" or "DECLINED" or "REJECTED" => new ChargeFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = webhook.Event,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = data?.TransactionResponseCode ?? data?.Status,
                        FailureMessage = data?.Status
                    },
                    "PAY" or "CAPTURE" => new ChargePendingEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Pending,
                        EventType = webhook.Event,
                        Category = WebhookEventCategory.ChargePending,
                        Amount = amount,
                        Currency = currency
                    },
                    "REFUND" => new RefundSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Refunded,
                        EventType = webhook.Event,
                        Category = WebhookEventCategory.RefundSucceeded,
                        RefundReference = reference,
                        Amount = amount,
                        Currency = currency,
                        IsPartial = false
                    },
                    "FAILED" or "DECLINED" => new ChargeFailedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Failed,
                        EventType = webhook.Event,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = data?.TransactionResponseCode ?? data?.Status,
                        FailureMessage = data?.Status
                    },
                    _ => new WebhookEvent
                    {
                        GatewayReference = reference,
                        Status = MapStatus(statusUpper),
                        EventType = webhook.Event,
                        Category = WebhookEventCategory.Unknown
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Kashier webhook");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    /// <summary>
    /// Build a hosted-payment-page redirect URL. Pure helper — for consumers who prefer Kashier's hosted page
    /// over the server <c>/checkout</c> call. Targets <c>https://checkout.kashier.io</c> with the documented
    /// query string and the signed order hash.
    /// </summary>
    public string BuildHostedPaymentUrl(string orderId, decimal amount, string? currency = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);
        var amt = amount.ToString("0.00", CultureInfo.InvariantCulture);
        var ccy = string.IsNullOrWhiteSpace(currency) ? _options.Currency : currency.ToUpperInvariant();
        var mode = _options.UseSandbox ? "test" : (string.IsNullOrWhiteSpace(_options.Mode) ? "live" : _options.Mode);
        var hash = ComputeOrderHash(_options.MerchantId, orderId, amt, ccy, HashKey);
        var baseUrl = (_options.HostedPaymentBaseUrl ?? KashierHttpClient.DefaultHostedPaymentBaseUrl).TrimEnd('/');

        var sb = new StringBuilder();
        sb.Append(baseUrl)
          .Append("?merchantId=").Append(Uri.EscapeDataString(_options.MerchantId))
          .Append("&orderId=").Append(Uri.EscapeDataString(orderId))
          .Append("&amount=").Append(Uri.EscapeDataString(amt))
          .Append("&currency=").Append(Uri.EscapeDataString(ccy))
          .Append("&hash=").Append(hash)
          .Append("&mode=").Append(Uri.EscapeDataString(mode));
        if (!string.IsNullOrWhiteSpace(_options.RedirectUrl))
            sb.Append("&merchantRedirect=").Append(Uri.EscapeDataString(_options.RedirectUrl));
        if (!string.IsNullOrWhiteSpace(_options.ServerWebhookUrl))
            sb.Append("&serverWebhook=").Append(Uri.EscapeDataString(_options.ServerWebhookUrl));
        sb.Append("&display=en");
        return sb.ToString();
    }

    /// <summary>
    /// Compute the Kashier order hash: HMAC-SHA256 (hex) over <c>/?payment={mid}.{orderId}.{amount}.{currency}</c>.
    /// </summary>
    public static string ComputeOrderHash(
        string merchantId, string orderId, string amount, string currency, string key)
    {
        var path = $"/?payment={merchantId}.{orderId}.{amount}.{currency}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key ?? string.Empty));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(path));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Key for the order hash. Per the official integration guide this is the Secret Key. (Kashier's GitHub demo
    /// and the asciisd SDK key the same hash with the Payment API Key; most merchant accounts have
    /// apiKey == secretKey, so both agree in practice.)
    /// </summary>
    // UNVERIFIED: docs say Secret Key; reference SDKs use API Key. We follow the written integration guide.
    private string HashKey => string.IsNullOrWhiteSpace(_options.SecretKey) ? _options.ApiKey : _options.SecretKey;

    /// <summary>Key Kashier uses to sign webhooks — the Payment API Key, unless a distinct webhook secret was issued.</summary>
    private string WebhookKey => string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.ApiKey : _options.WebhookSecret;

    private static string Rfc3986Escape(string value) => Uri.EscapeDataString(value);

    private static PaymentStatus MapStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "SUCCESS" or "CAPTURED" or "PAID" or "COMPLETED" or "APPROVED" => PaymentStatus.Completed,
        "PENDING" or "PROCESSING" or "INPROGRESS" or "INITIATED" => PaymentStatus.Pending,
        "FAILED" or "DECLINED" or "REJECTED" => PaymentStatus.Failed,
        "CANCELED" or "CANCELLED" or "VOIDED" => PaymentStatus.Cancelled,
        "REFUNDED" or "PARTIAL_REFUNDED" or "PARTIALLYREFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Kashier API response shapes (internal) ===
    // Field names per developers.kashier.io (order reconciliation / webhook) and the asciisd Checkout.php
    // response handling (response.card.result / response.card.3DSecure / messages.en).

    private sealed class KashierCheckoutResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("messages")] public KashierMessages? Messages { get; set; }
        [JsonPropertyName("response")] public KashierResponseData? Response { get; set; }
    }

    private sealed class KashierMessages
    {
        [JsonPropertyName("en")] public string? En { get; set; }
    }

    private sealed class KashierResponseData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("kashierOrderId")] public string? KashierOrderId { get; set; }
        [JsonPropertyName("merchantOrderId")] public string? MerchantOrderId { get; set; }
        [JsonPropertyName("orderReference")] public string? OrderReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("transactionResponseCode")] public string? TransactionResponseCode { get; set; }
        [JsonPropertyName("redirectUrl")] public string? RedirectUrl { get; set; }
        [JsonPropertyName("card")] public KashierCardResult? Card { get; set; }
    }

    private sealed class KashierCardResult
    {
        [JsonPropertyName("result")] public string? Result { get; set; }
        // UNVERIFIED: exact 3DS field names inside response.card are not fully documented publicly.
        [JsonPropertyName("3DSecure")] public string? ThreeDSecure { get; set; }
        [JsonPropertyName("acsUrl")] public string? AcsUrl { get; set; }
    }

    private sealed class KashierWebhookEvent
    {
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("data")] public KashierWebhookData? Data { get; set; }
    }

    private sealed class KashierWebhookData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("kashierOrderId")] public string? KashierOrderId { get; set; }
        [JsonPropertyName("merchantOrderId")] public string? MerchantOrderId { get; set; }
        [JsonPropertyName("orderReference")] public string? OrderReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("method")] public string? Method { get; set; }
        [JsonPropertyName("transactionResponseCode")] public string? TransactionResponseCode { get; set; }
        [JsonPropertyName("shopperReference")] public string? ShopperReference { get; set; }
    }
}
