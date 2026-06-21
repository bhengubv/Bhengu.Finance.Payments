// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayJustNow.Providers;

/// <summary>
/// PayJustNow Buy-Now-Pay-Later (BNPL) provider — 3 interest-free instalments for South African
/// consumers. Redirect flow: <c>ProcessPaymentAsync</c> creates a checkout and returns a
/// <see cref="PaymentResponse.RedirectUrl"/> the payer must be sent to; PayJustNow then redirects the
/// browser back to the merchant's success/fail callback URL.
/// <para>
/// Wire format verified against PayJustNow's official, public production source: the
/// PayJustNow-for-WooCommerce gateway (WordPress.org plugin SVN, stable tag 2.7.9,
/// <c>classes/payjustnow.class.php</c>) and the PayJustNow public API README
/// (github.com/PayJustNow/Api). The documented merchant API surface is exactly two server-to-server
/// operations: <c>POST /api/v1/merchant/checkout</c> and <c>POST /api/v1/merchant/refund</c>.
/// </para>
/// <para>
/// PayJustNow does NOT expose a recurring/subscription, mandate/debit-order, or payout API, nor a
/// cryptographically-signed server webhook — its only inbound notification is a browser redirect to the
/// merchant's callback URL. Those capabilities are therefore intentionally not advertised or implemented
/// (see <see cref="VerifyWebhookSignature"/> / <see cref="ParseWebhookAsync"/>) rather than guessed.
/// </para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format verified against PayJustNow's public production WooCommerce gateway (payjustnow.class.php v2.7.9) + public API README; never sandbox-verified by us.")]
public sealed class PayJustNowPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayJustNowOptions _options;
    private readonly PayJustNowIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayJustNow;

    /// <summary>
    /// Capabilities reflect PayJustNow's documented merchant API: a card-backed BNPL charge via a
    /// hosted redirect (checkout) and full/partial refunds. No payout, subscription, mandate, signed
    /// webhook, or sync-settlement surface exists in the public API, so none is advertised.
    /// </summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.Idempotency;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PayJustNowPaymentProvider(
        HttpClient httpClient,
        IOptions<PayJustNowOptions> options,
        ILogger<PayJustNowPaymentProvider> logger,
        PayJustNowIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.MerchantId)} is required");

        PayJustNowHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency,
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPaymentCoreAsync(request, ct), ct),
            ct);
    }

    // Source: payjustnow.class.php L504–L558 + L600–L604 (request shape, POST 'checkout', response shape).
    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var amountInCents = (int)(request.Amount * 100m);
        var meta = request.Metadata;

        var requestBody = new CheckoutRequest
        {
            Customer = new CheckoutCustomer
            {
                FirstName = meta?.GetValueOrDefault("first_name") ?? string.Empty,
                LastName = meta?.GetValueOrDefault("last_name") ?? string.Empty,
                Email = meta?.GetValueOrDefault("email") ?? string.Empty,
                MobileNumber = meta?.GetValueOrDefault("mobile_number") ?? string.Empty,
                Address = new CheckoutAddress
                {
                    AddressLine1 = meta?.GetValueOrDefault("address_line_1"),
                    AddressLine2 = meta?.GetValueOrDefault("address_line_2"),
                    City = meta?.GetValueOrDefault("city"),
                    Province = meta?.GetValueOrDefault("province"),
                    PostalCode = meta?.GetValueOrDefault("postal_code"),
                    Country = meta?.GetValueOrDefault("country")
                }
            },
            Order = new CheckoutOrder
            {
                Amount = amountInCents,
                MerchantReference = meta?.GetValueOrDefault("order_id")
                    ?? meta?.GetValueOrDefault("merchant_reference")
                    ?? Guid.NewGuid().ToString("N"),
                SuccessCallbackUrl = meta?.GetValueOrDefault("success_callback_url"),
                FailCallbackUrl = meta?.GetValueOrDefault("fail_callback_url"),
                // A single line item describing the order. PayJustNow requires at least one item; richer
                // baskets can be supplied by the caller building the request upstream where line detail exists.
                Items = new[]
                {
                    new CheckoutItem
                    {
                        MerchantReference = meta?.GetValueOrDefault("order_id")
                            ?? meta?.GetValueOrDefault("merchant_reference")
                            ?? "item",
                        Quantity = 1,
                        Description = request.Description,
                        UnitPrice = amountInCents
                    }
                }
            }
        };

        var body = await PayJustNowHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "checkout", requestBody, "ProcessPayment", ct).ConfigureAwait(false);
        var pjnResponse = JsonSerializer.Deserialize<CheckoutResponse>(body, PayJustNowHttpClient.Json);
        var data = pjnResponse?.Data;

        if (data is null || string.IsNullOrEmpty(data.Token) || string.IsNullOrEmpty(data.RedirectTo))
        {
            // Source: payjustnow.class.php L592–L598, L634–L639 — a missing token/redirect (or a top-level
            // 'message') is PayJustNow's failure shape for an unauthorised or rejected checkout.
            var message = pjnResponse?.Message ?? "PayJustNow checkout returned no token/redirect";
            throw new PaymentDeclinedException(ProviderName, "checkout_failed", message);
        }

        Logger.LogInformation("PayJustNow checkout created: token={Token}", data.Token);

        return new PaymentResponse
        {
            // The checkout token is PayJustNow's reference for this order (carried into the refund as 'token').
            GatewayReference = data.Token,
            // BNPL settles asynchronously: the payer must complete the hosted flow, after which PayJustNow
            // redirects to the success/fail callback. Treat the created checkout as Pending until then.
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = data.RedirectTo,
            Message = "BNPL checkout created"
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference,
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessRefundCoreAsync(request, ct), ct),
            ct);
    }

    // Source: payjustnow.class.php L714–L750 (request shape + POST 'refund') and L778–L820 (response shapes).
    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var amountInCents = (int)(request.Amount * 100m);

        // PayJustNow distinguishes "full" vs "partial" refunds by an explicit 'type' field. We only know it
        // is partial when the caller supplies OriginalAmount (RefundRequest.IsPartial); otherwise default to
        // "full" — matching the plugin, which compares the refund amount to the order total.
        var refundType = request.IsPartial ? "partial" : "full";

        var requestBody = new RefundRequestBody
        {
            // 'token' is the checkout token returned by ProcessPaymentAsync (GatewayReference).
            Token = request.GatewayReference,
            // 'merchant_reference' is required by the API; we don't independently track it here, so echo the
            // token. Callers that retained their order reference can pass it via metadata upstream.
            // UNVERIFIED: whether 'merchant_reference' must equal the original checkout's order reference, or is
            // advisory, is not stated in the public docs — the plugin always sends the WooCommerce order id.
            MerchantReference = request.GatewayReference,
            Type = refundType,
            Amount = amountInCents,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? "No reason supplied." : request.Reason
        };

        var body = await PayJustNowHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "refund", requestBody, "ProcessRefund", ct).ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<RefundResponseBody>(body, PayJustNowHttpClient.Json);

        // Source: payjustnow.class.php L794–L805 — status "FAILED" carries an errors payload; treat as declined.
        if (string.Equals(refundResponse?.Status, "FAILED", StringComparison.OrdinalIgnoreCase))
        {
            var reason = refundResponse?.Reason?.Errors?.Message ?? "PayJustNow refund failed";
            throw new PaymentDeclinedException(ProviderName, "refund_failed", reason);
        }

        var refundedCents = refundResponse?.AmountRefunded ?? amountInCents;

        Logger.LogInformation("PayJustNow refund {Status} for token {Token}",
            refundResponse?.Status, request.GatewayReference);

        return new RefundResponse
        {
            // PayJustNow's refund response does not return a distinct refund id; the checkout token remains the
            // stable reference for the refunded order.
            GatewayReference = request.GatewayReference,
            Amount = refundedCents / 100m,
            Status = MapStatus(refundResponse?.Status ?? "pending"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Reason?.Text
        };
    }

    /// <summary>
    /// PayJustNow does not provide a cryptographically-signed server webhook: its only inbound
    /// notification is a browser redirect to the merchant's success/fail callback URL (query parameters
    /// such as <c>status</c>, verified by the merchant-generated key the plugin echoes — payjustnow.class.php
    /// L105–L194). There is no documented HMAC/signature to verify, so this always returns <c>false</c>.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);
        return RunWebhookVerify(() => false);
    }

    /// <summary>
    /// PayJustNow has no documented machine-readable webhook event schema (settlement is signalled by a
    /// browser redirect to the merchant callback, not a JSON event). To avoid inventing a wire shape this
    /// returns <c>null</c>; consumers reconcile via the redirect callback / a follow-up refund-or-charge
    /// lookup instead.
    /// </summary>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Task.FromResult<WebhookEvent?>(null);
    }

    // Source: payjustnow.class.php L778–L807 — refund status strings are "REFUNDED" / "FAILED".
    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "refunded" => PaymentStatus.Refunded,
        "approved" or "completed" or "success" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" or "cancelled" or "canceled" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    // === PayJustNow merchant API wire shapes (internal) ===
    // checkout request: payjustnow.class.php L504–L529; checkout response: L600–L604.

    private sealed class CheckoutRequest
    {
        [JsonPropertyName("customer")] public CheckoutCustomer Customer { get; set; } = new();
        [JsonPropertyName("order")] public CheckoutOrder Order { get; set; } = new();
    }

    private sealed class CheckoutCustomer
    {
        [JsonPropertyName("first_name")] public string FirstName { get; set; } = string.Empty;
        [JsonPropertyName("last_name")] public string LastName { get; set; } = string.Empty;
        [JsonPropertyName("email")] public string Email { get; set; } = string.Empty;
        [JsonPropertyName("mobile_number")] public string MobileNumber { get; set; } = string.Empty;
        [JsonPropertyName("address")] public CheckoutAddress Address { get; set; } = new();
    }

    private sealed class CheckoutAddress
    {
        [JsonPropertyName("address_line_1")] public string? AddressLine1 { get; set; }
        [JsonPropertyName("address_line_2")] public string? AddressLine2 { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("province")] public string? Province { get; set; }
        [JsonPropertyName("postal_code")] public string? PostalCode { get; set; }
        [JsonPropertyName("country")] public string? Country { get; set; }
    }

    private sealed class CheckoutOrder
    {
        [JsonPropertyName("amount")] public int Amount { get; set; }
        [JsonPropertyName("merchant_reference")] public string MerchantReference { get; set; } = string.Empty;
        [JsonPropertyName("success_callback_url")] public string? SuccessCallbackUrl { get; set; }
        [JsonPropertyName("fail_callback_url")] public string? FailCallbackUrl { get; set; }
        [JsonPropertyName("items")] public CheckoutItem[] Items { get; set; } = [];
    }

    private sealed class CheckoutItem
    {
        [JsonPropertyName("merchant_reference")] public string MerchantReference { get; set; } = string.Empty;
        [JsonPropertyName("quantity")] public int Quantity { get; set; }
        [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
        [JsonPropertyName("unit_price")] public int UnitPrice { get; set; }
    }

    private sealed class CheckoutResponse
    {
        [JsonPropertyName("data")] public CheckoutResponseData? Data { get; set; }
        // Present on an error (e.g. wrong credentials): payjustnow.class.php L592.
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class CheckoutResponseData
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expires_at")] public string? ExpiresAt { get; set; }
        [JsonPropertyName("redirect_to")] public string? RedirectTo { get; set; }
    }

    // refund request: payjustnow.class.php L715–L721; refund response: L778–L779.

    private sealed class RefundRequestBody
    {
        [JsonPropertyName("merchant_reference")] public string MerchantReference { get; set; } = string.Empty;
        [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("type")] public string Type { get; set; } = "full";
        [JsonPropertyName("amount")] public int Amount { get; set; }
        [JsonPropertyName("reason")] public string Reason { get; set; } = string.Empty;
    }

    private sealed class RefundResponseBody
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("refunded_at")] public string? RefundedAt { get; set; }
        [JsonPropertyName("amount_refunded")] public int? AmountRefunded { get; set; }
        // 'reason' is a free-text string on success but an object ({ errors: { code, message } }) on failure
        // (payjustnow.class.php L778–L779). A custom converter normalises both shapes.
        [JsonPropertyName("reason")]
        [JsonConverter(typeof(RefundReasonConverter))]
        public RefundReason? Reason { get; set; }
    }

    private sealed class RefundReason
    {
        public string? Text { get; set; }
        public RefundErrors? Errors { get; set; }
    }

    private sealed class RefundErrors
    {
        [JsonPropertyName("code")] public int? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    /// <summary>
    /// Reads PayJustNow's polymorphic refund <c>reason</c>: a JSON string on success, or an object
    /// <c>{ "errors": { "code", "message" } }</c> on failure.
    /// </summary>
    private sealed class RefundReasonConverter : JsonConverter<RefundReason?>
    {
        public override RefundReason? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.String:
                    return new RefundReason { Text = reader.GetString() };
                case JsonTokenType.StartObject:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        var reason = new RefundReason();
                        if (doc.RootElement.TryGetProperty("errors", out var errors)
                            && errors.ValueKind == JsonValueKind.Object)
                        {
                            reason.Errors = new RefundErrors
                            {
                                Code = errors.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : null,
                                Message = errors.TryGetProperty("message", out var m) ? m.GetString() : null
                            };
                        }
                        return reason;
                    }
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, RefundReason? value, JsonSerializerOptions options)
        {
            if (value?.Text is not null) writer.WriteStringValue(value.Text);
            else writer.WriteNullValue();
        }
    }
}
