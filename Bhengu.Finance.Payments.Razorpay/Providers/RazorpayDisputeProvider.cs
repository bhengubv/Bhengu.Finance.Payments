// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay disputes / chargebacks provider. Wraps the <c>/v1/disputes</c>,
/// <c>/v1/disputes/{id}/contest</c>, and <c>/v1/disputes/{id}/accept</c> endpoints.
/// </summary>
public sealed class RazorpayDisputeProvider : IDisputeProvider
{
    private readonly RazorpayHttpClient _http;
    private readonly ILogger<RazorpayDisputeProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new dispute provider bound to the supplied HTTP client and options.</summary>
    public RazorpayDisputeProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayDisputeProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new RazorpayHttpClient(httpClient, options.Value, ProviderName, logger);
    }

    /// <inheritdoc />
    public async Task<Dispute?> GetDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disputeReference);

        try
        {
            var raw = await _http.GetAsync($"v1/disputes/{Uri.EscapeDataString(disputeReference)}", ct, "GetDispute").ConfigureAwait(false);
            var d = RazorpayHttpClient.DeserialiseOrThrow<RazorpayDispute>(raw, ProviderName, "GetDispute");
            return MapDispute(d);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Dispute> ListDisputesAsync(DateTime? fromUtc = null, DateTime? toUtc = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var query = new List<string> { "count=100" };
        if (fromUtc.HasValue)
            query.Add($"from={new DateTimeOffset(DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}");
        if (toUtc.HasValue)
            query.Add($"to={new DateTimeOffset(DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}");

        var path = "v1/disputes?" + string.Join("&", query);
        var raw = await _http.GetAsync(path, ct, "ListDisputes").ConfigureAwait(false);
        var collection = RazorpayHttpClient.DeserialiseOrThrow<RazorpayDisputeCollection>(raw, ProviderName, "ListDisputes");

        if (collection.Items is null) yield break;
        foreach (var d in collection.Items)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapDispute(d);
        }
    }

    /// <inheritdoc />
    public async Task<Dispute> SubmitEvidenceAsync(string disputeReference, DisputeEvidence evidence, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disputeReference);
        ArgumentNullException.ThrowIfNull(evidence);

        var body = new
        {
            summary = evidence.Explanation,
            shipping_proof = evidence.ShippingTrackingNumber,
            billing_proof = evidence.BillingAddress,
            cancellation_proof = (string?)null,
            customer_communication = evidence.ProviderEvidenceFields?.GetValueOrDefault("customer_communication"),
            proof_of_service = evidence.ProviderEvidenceFields?.GetValueOrDefault("proof_of_service"),
            explanation_letter = evidence.Explanation,
            refund_confirmation = (string?)null,
            access_activity_log = (string?)null,
            refund_cancellation_policy = (string?)null,
            term_and_conditions = (string?)null,
            others = evidence.ProviderEvidenceFields?.GetValueOrDefault("others"),
            action = "draft"
        };

        var raw = await _http.SendAsync(HttpMethod.Patch, $"v1/disputes/{Uri.EscapeDataString(disputeReference)}/contest", body, ct, "ContestDispute").ConfigureAwait(false);
        var d = RazorpayHttpClient.DeserialiseOrThrow<RazorpayDispute>(raw, ProviderName, "ContestDispute");

        _logger.LogInformation("Razorpay dispute contested: {DisputeId}", d.Id);
        return MapDispute(d);
    }

    /// <inheritdoc />
    public async Task<Dispute> AcceptDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disputeReference);

        var raw = await _http.SendAsync(HttpMethod.Post, $"v1/disputes/{Uri.EscapeDataString(disputeReference)}/accept", body: null, ct, "AcceptDispute").ConfigureAwait(false);
        var d = RazorpayHttpClient.DeserialiseOrThrow<RazorpayDispute>(raw, ProviderName, "AcceptDispute");

        _logger.LogInformation("Razorpay dispute accepted: {DisputeId}", d.Id);
        return MapDispute(d);
    }

    private static Dispute MapDispute(RazorpayDispute d)
    {
        var status = d.Status?.ToLowerInvariant() switch
        {
            "open" => DisputeStatus.NeedsResponse,
            "under_review" => DisputeStatus.UnderReview,
            "won" => DisputeStatus.Won,
            "lost" => DisputeStatus.Lost,
            "closed" => DisputeStatus.Lost,
            "accepted" => DisputeStatus.Accepted,
            _ => DisputeStatus.NeedsResponse
        };

        return new Dispute
        {
            Reference = d.Id ?? string.Empty,
            ChargeReference = d.PaymentId ?? string.Empty,
            Amount = d.Amount / 100m,
            Currency = d.Currency ?? "INR",
            Status = status,
            ReasonCode = d.ReasonCode,
            ReasonDescription = d.ReasonDescription,
            OpenedAt = d.CreatedAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(d.CreatedAt.Value).UtcDateTime : DateTime.UtcNow,
            EvidenceDueBy = d.RespondBy is > 0 ? DateTimeOffset.FromUnixTimeSeconds(d.RespondBy.Value).UtcDateTime : null,
            ChargebackFee = d.DeductAtOnset is > 0 ? d.DeductAtOnset / 100m : null
        };
    }

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayDisputeCollection
    {
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("items")] public List<RazorpayDispute>? Items { get; set; }
    }

    private sealed class RazorpayDispute
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("payment_id")] public string? PaymentId { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("amount_deducted")] public long DeductAtOnset { get; set; }
        [JsonPropertyName("reason_code")] public string? ReasonCode { get; set; }
        [JsonPropertyName("reason_description")] public string? ReasonDescription { get; set; }
        [JsonPropertyName("respond_by")] public long? RespondBy { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("phase")] public string? Phase { get; set; }
        [JsonPropertyName("created_at")] public long? CreatedAt { get; set; }
    }
}
