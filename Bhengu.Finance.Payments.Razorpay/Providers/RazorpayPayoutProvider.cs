// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Dedicated RazorpayX payout provider. Wraps the <c>/v1/payouts</c>, <c>/v1/contacts</c>, and
/// <c>/v1/fund_accounts</c> endpoints and honours idempotency-key passthrough.
/// </summary>
/// <remarks>
/// This is the new single-responsibility implementation. The legacy
/// <see cref="RazorpayPaymentProvider"/> also implements <see cref="IPayoutProvider"/> for
/// backwards-compatibility; new consumers should prefer this provider via DI keyed lookup.
/// </remarks>
public sealed class RazorpayPayoutProvider : BhenguProviderBase, IPayoutProvider
{
    private readonly RazorpayHttpClient _http;
    private readonly RazorpayOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new payout provider bound to the supplied HTTP client and options.</summary>
    public RazorpayPayoutProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayPayoutProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _http = new RazorpayHttpClient(httpClient, _options, ProviderName, logger);
    }

    /// <inheritdoc />
    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.RazorpayXAccountNumber))
            throw new ProviderConfigurationException(ProviderName,
                $"{nameof(RazorpayOptions.RazorpayXAccountNumber)} is required for RazorpayX payouts");

        var amountInPaise = (long)(request.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? _options.Currency
            : request.Currency.ToUpperInvariant();

        var body = new
        {
            account_number = _options.RazorpayXAccountNumber,
            fund_account_id = request.DestinationToken,
            amount = amountInPaise,
            currency,
            mode = request.Metadata?.GetValueOrDefault("mode") ?? "IMPS",
            purpose = request.Metadata?.GetValueOrDefault("purpose") ?? "payout",
            queue_if_low_balance = true,
            reference_id = request.Metadata?.GetValueOrDefault("reference_id") ?? $"payout-{Guid.NewGuid():N}",
            narration = request.Description,
            notes = request.Metadata
        };

        var raw = await _http.SendAsync(HttpMethod.Post, "v1/payouts", body, ct, "ProcessPayout", request.IdempotencyKey).ConfigureAwait(false);
        var payout = RazorpayHttpClient.DeserialiseOrThrow<RazorpayPayout>(raw, ProviderName, "ProcessPayout");

        Logger.LogInformation("Razorpay payout created: {PayoutId} status={Status}", payout.Id, payout.Status);

        return new PayoutResponse
        {
            GatewayReference = payout.Id ?? string.Empty,
            Status = MapStatus(payout.Status),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "processed" or "completed" => PaymentStatus.Completed,
        "queued" or "pending" or "scheduled" or "initiated" or "processing" => PaymentStatus.Pending,
        "failed" or "rejected" or "reversed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayPayout
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("mode")] public string? Mode { get; set; }
    }
}
