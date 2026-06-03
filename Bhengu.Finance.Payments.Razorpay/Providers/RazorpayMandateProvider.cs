// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay mandate provider — wraps eMandate, UPI Autopay, and NACH recurring rails.
/// </summary>
/// <remarks>
/// Razorpay's recurring-debit model uses a two-step flow: (1) create a registration order
/// (<c>POST /v1/orders</c> with <c>method=emandate</c>), the payer authorises via redirect /
/// UPI app, then (2) future debits go via <c>POST /v1/subscriptions/{id}/charge</c> against
/// the resulting token. We expose that two-step under the SDK's unified
/// <see cref="IMandateProvider"/> shape.
/// </remarks>
public sealed class RazorpayMandateProvider : IMandateProvider
{
    private readonly RazorpayHttpClient _http;
    private readonly ILogger<RazorpayMandateProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new mandate provider bound to the supplied HTTP client and options.</summary>
    public RazorpayMandateProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayMandateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new RazorpayHttpClient(httpClient, options.Value, ProviderName, logger);
    }

    /// <inheritdoc />
    public async Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Razorpay eMandate registration: create an order with method=emandate.
        // The amount on registration must be 0 (or 1) for authorisation only.
        var orderBody = new
        {
            amount = 0,
            currency = request.Currency.ToUpperInvariant(),
            method = "emandate",
            customer_id = request.CustomerId,
            token = new
            {
                auth_type = "netbanking",
                max_amount = (long)(request.AmountLimit * 100),
                expire_at = request.EndAt is null ? (long?)null : new DateTimeOffset(DateTime.SpecifyKind(request.EndAt.Value, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                bank_account = string.IsNullOrWhiteSpace(request.BankAccountToken) ? null : new { account_number = request.BankAccountToken }
            },
            receipt = $"mandate-{Guid.NewGuid():N}",
            notes = new Dictionary<string, string> { ["description"] = request.Description }
        };

        var raw = await _http.SendAsync(HttpMethod.Post, "v1/orders", orderBody, ct, "CreateMandateOrder", request.IdempotencyKey).ConfigureAwait(false);
        var order = RazorpayHttpClient.DeserialiseOrThrow<RazorpayMandateOrder>(raw, ProviderName, "CreateMandateOrder");

        _logger.LogInformation("Razorpay mandate registration order created: {OrderId}", order.Id);

        return new Mandate
        {
            Reference = order.Id ?? string.Empty,
            CustomerId = request.CustomerId,
            Status = MandateStatus.Pending,
            AmountLimit = request.AmountLimit,
            Currency = request.Currency.ToUpperInvariant(),
            AuthorisedAt = null,
            CancelledAt = null,
            // Razorpay returns the auth URL only after the payer enters the checkout — surface the
            // order id; consumers redirect via Razorpay Checkout (subscription_id / order_id flow).
            AuthorisationUrl = order.ShortUrl
        };
    }

    /// <inheritdoc />
    public async Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mandateReference);

        try
        {
            // Once authorised, the mandate lives as a token. We look up the token directly.
            var raw = await _http.GetAsync($"v1/tokens/{Uri.EscapeDataString(mandateReference)}", ct, "GetMandate").ConfigureAwait(false);
            var t = RazorpayHttpClient.DeserialiseOrThrow<RazorpayMandateToken>(raw, ProviderName, "GetMandate");
            return MapMandate(t);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mandateReference);

        // Razorpay needs the customer id to delete a token. Fetch first.
        var existing = await GetMandateAsync(mandateReference, ct).ConfigureAwait(false);
        if (existing is null)
            throw new BhenguPaymentException(ProviderName, $"Mandate {mandateReference} not found");
        if (existing.Status is MandateStatus.Cancelled or MandateStatus.Expired)
            return existing;

        try
        {
            await _http.DeleteAsync($"v1/customers/{Uri.EscapeDataString(existing.CustomerId)}/tokens/{Uri.EscapeDataString(mandateReference)}", ct, "CancelMandate").ConfigureAwait(false);
            return existing with { Status = MandateStatus.Cancelled, CancelledAt = DateTime.UtcNow };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return existing with { Status = MandateStatus.Cancelled };
        }
    }

    /// <inheritdoc />
    public async Task<PaymentResponse> ChargeMandateAsync(MandateChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Recurring debit against an authorised mandate token: POST /v1/payments/create/recurring.
        var body = new
        {
            email = (string?)null,
            contact = (string?)null,
            amount = (long)(request.Amount * 100),
            currency = request.Currency.ToUpperInvariant(),
            order_id = (string?)null,
            customer_id = (string?)null,
            token = request.MandateReference,
            recurring = "1",
            description = request.Description,
            notes = new Dictionary<string, string> { ["mandate"] = request.MandateReference }
        };

        var raw = await _http.SendAsync(HttpMethod.Post, "v1/payments/create/recurring", body, ct, "ChargeMandate", request.IdempotencyKey).ConfigureAwait(false);
        var p = RazorpayHttpClient.DeserialiseOrThrow<RazorpayRecurringPayment>(raw, ProviderName, "ChargeMandate");

        _logger.LogInformation("Razorpay mandate debit created: {PaymentId} status={Status}", p.Id, p.Status);

        return new PaymentResponse
        {
            GatewayReference = p.Id ?? string.Empty,
            Status = MapPaymentStatus(p.Status),
            Amount = request.Amount,
            Currency = request.Currency.ToUpperInvariant(),
            ProcessedAt = DateTime.UtcNow,
            Message = p.Status
        };
    }

    private static Mandate MapMandate(RazorpayMandateToken t)
    {
        var status = t.Status?.ToLowerInvariant() switch
        {
            "confirmed" or "active" => MandateStatus.Active,
            "initiated" => MandateStatus.Pending,
            "rejected" => MandateStatus.Rejected,
            "cancelled" or "canceled" => MandateStatus.Cancelled,
            "expired" => MandateStatus.Expired,
            "paused" => MandateStatus.Paused,
            _ => MandateStatus.Pending
        };

        return new Mandate
        {
            Reference = t.Id ?? string.Empty,
            CustomerId = t.CustomerId ?? string.Empty,
            Status = status,
            AmountLimit = (t.MaxAmount ?? 0L) / 100m,
            Currency = "INR",
            AuthorisedAt = t.ConfirmedAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.ConfirmedAt.Value).UtcDateTime : null,
            CancelledAt = t.CancelledAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(t.CancelledAt.Value).UtcDateTime : null
        };
    }

    private static PaymentStatus MapPaymentStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "captured" or "authorized" or "paid" or "processed" => PaymentStatus.Completed,
        "created" or "pending" or "queued" => PaymentStatus.Pending,
        "failed" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayMandateOrder
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("short_url")] public string? ShortUrl { get; set; }
    }

    private sealed class RazorpayMandateToken
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("customer_id")] public string? CustomerId { get; set; }
        [JsonPropertyName("method")] public string? Method { get; set; }
        [JsonPropertyName("recurring_status")] public string? Status { get; set; }
        [JsonPropertyName("max_amount")] public long? MaxAmount { get; set; }
        [JsonPropertyName("confirmed_at")] public long? ConfirmedAt { get; set; }
        [JsonPropertyName("cancelled_at")] public long? CancelledAt { get; set; }
    }

    private sealed class RazorpayRecurringPayment
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("razorpay_payment_id")] public string? AltId { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
