// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paystack.Providers;

/// <summary>
/// Paystack implementation of <see cref="IDisputeProvider"/> backed by Paystack's
/// <c>/dispute</c> endpoints.
/// </summary>
/// <remarks>
/// Paystack disputes carry an evidence-deadline (<c>due_at</c>) the SDK exposes via
/// <see cref="Dispute.EvidenceDueBy"/>. Evidence submission goes through <c>/dispute/:id/resolve</c>
/// which accepts a <c>resolution</c>, optional <c>uploaded_filename</c> and refund amount when applicable.
/// </remarks>
public sealed class PaystackDisputeProvider : IDisputeProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;
    private readonly ILogger<PaystackDisputeProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Paystack;

    /// <summary>Construct a dispute provider. Designed to be registered via DI.</summary>
    public PaystackDisputeProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackDisputeProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        PaystackHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public async Task<Dispute?> GetDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(disputeReference);
        try
        {
            var responseBody = await PaystackHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, $"dispute/{Uri.EscapeDataString(disputeReference)}", null, "GetDispute", ct).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaystackDisputeResponse>(responseBody, PaystackHttpClient.Json);
            return response?.Data is { } dispute ? MapDispute(dispute) : null;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Dispute>> ListDisputesAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
    {
        var qs = new StringBuilder("dispute?perPage=100");
        if (fromUtc.HasValue)
            qs.Append("&from=").Append(Uri.EscapeDataString(fromUtc.Value.ToString("o", CultureInfo.InvariantCulture)));
        if (toUtc.HasValue)
            qs.Append("&to=").Append(Uri.EscapeDataString(toUtc.Value.ToString("o", CultureInfo.InvariantCulture)));

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Get, qs.ToString(), null, "ListDisputes", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackDisputeListResponse>(responseBody, PaystackHttpClient.Json);
        if (response?.Data is null)
            return Array.Empty<Dispute>();

        var result = new List<Dispute>(response.Data.Count);
        foreach (var d in response.Data) result.Add(MapDispute(d));
        return result;
    }

    /// <inheritdoc/>
    public async Task<Dispute> SubmitEvidenceAsync(string disputeReference, DisputeEvidence evidence, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(disputeReference);
        ArgumentNullException.ThrowIfNull(evidence);

        var body = new
        {
            customer_email = evidence.CustomerEmailAddress,
            customer_name = evidence.CustomerName,
            customer_phone = evidence.ProviderEvidenceFields?.GetValueOrDefault("customer_phone"),
            service_details = evidence.Explanation,
            delivery_address = evidence.ShippingAddress,
            delivery_date = evidence.ShippingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Put, $"dispute/{Uri.EscapeDataString(disputeReference)}/evidence", body, "SubmitEvidence", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackDisputeResponse>(responseBody, PaystackHttpClient.Json);

        if (response?.Data is null)
        {
            // Fallback: refetch via GET so callers still receive a populated record.
            var refetched = await GetDisputeAsync(disputeReference, ct).ConfigureAwait(false);
            return refetched ?? throw new BhenguPaymentException(ProviderName, "Paystack accepted evidence but no dispute was returned", "no_dispute_data");
        }

        return MapDispute(response.Data) with { Status = DisputeStatus.UnderReview };
    }

    /// <inheritdoc/>
    public async Task<Dispute> AcceptDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(disputeReference);
        // Paystack treats acceptance as a resolve with resolution=merchant-accepted.
        var existing = await GetDisputeAsync(disputeReference, ct).ConfigureAwait(false)
            ?? throw new BhenguPaymentException(ProviderName, $"Dispute {disputeReference} not found", "dispute_not_found");

        var body = new
        {
            resolution = "merchant-accepted",
            message = "Merchant accepts the dispute without contesting.",
            refund_amount = (long)(existing.Amount * 100m)
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Put, $"dispute/{Uri.EscapeDataString(disputeReference)}/resolve", body, "AcceptDispute", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackDisputeResponse>(responseBody, PaystackHttpClient.Json);
        if (response?.Data is null)
            return existing with { Status = DisputeStatus.Accepted };
        return MapDispute(response.Data) with { Status = DisputeStatus.Accepted };
    }

    private static Dispute MapDispute(PaystackDisputeData d) => new()
    {
        Reference = d.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        ChargeReference = d.Transaction?.Reference ?? string.Empty,
        Amount = d.RefundAmount.HasValue ? d.RefundAmount.Value / 100m : (d.Transaction?.Amount ?? 0L) / 100m,
        Currency = d.Currency ?? d.Transaction?.Currency ?? "NGN",
        Status = MapStatus(d.Status, d.Resolution),
        ReasonCode = d.Category,
        ReasonDescription = d.Status,
        OpenedAt = d.CreatedAt ?? DateTime.UtcNow,
        EvidenceDueBy = d.DueAt,
        ChargebackFee = null
    };

    private static DisputeStatus MapStatus(string? rawStatus, string? resolution) => (rawStatus?.ToLowerInvariant(), resolution?.ToLowerInvariant()) switch
    {
        ("resolved", "merchant-accepted") => DisputeStatus.Accepted,
        ("resolved", "declined") or ("resolved", "merchant-won") => DisputeStatus.Won,
        ("resolved", "lost") or ("resolved", "merchant-lost") => DisputeStatus.Lost,
        ("resolved", _) => DisputeStatus.Won,
        ("awaiting-merchant-feedback" or "pending", _) => DisputeStatus.NeedsResponse,
        ("awaiting-bank-feedback" or "under-review", _) => DisputeStatus.UnderReview,
        ("arbitration", _) => DisputeStatus.Arbitration,
        ("expired", _) => DisputeStatus.Expired,
        _ => DisputeStatus.NeedsResponse
    };

    // === Paystack API shapes (internal) ===

    private sealed class PaystackDisputeResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public PaystackDisputeData? Data { get; set; }
    }

    private sealed class PaystackDisputeListResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public List<PaystackDisputeData>? Data { get; set; }
    }

    private sealed class PaystackDisputeData
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("resolution")] public string? Resolution { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("refund_amount")] public long? RefundAmount { get; set; }
        [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("due_at")] public DateTime? DueAt { get; set; }
        [JsonPropertyName("transaction")] public PaystackDisputeTransaction? Transaction { get; set; }
    }

    private sealed class PaystackDisputeTransaction
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}
