// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Dispute;

/// <summary>
/// Evidence submitted to contest a dispute. Fields are intentionally generic across schemes;
/// the underlying provider will map to its own evidence categories (Stripe DisputeEvidence,
/// Paystack Resolve, etc.). Pass <see cref="ProviderEvidenceFields"/> for scheme-specific extras.
/// </summary>
public sealed record DisputeEvidence
{
    /// <summary>Free-text explanation of why the charge is legitimate.</summary>
    public string? Explanation { get; init; }

    /// <summary>Customer name on file.</summary>
    public string? CustomerName { get; init; }

    /// <summary>Customer email on file.</summary>
    public string? CustomerEmailAddress { get; init; }

    /// <summary>Billing address used at checkout.</summary>
    public string? BillingAddress { get; init; }

    /// <summary>Shipping address used at fulfilment.</summary>
    public string? ShippingAddress { get; init; }

    /// <summary>Carrier tracking number proving the goods were delivered.</summary>
    public string? ShippingTrackingNumber { get; init; }

    /// <summary>Carrier name (e.g. "DHL", "PostNet").</summary>
    public string? ShippingCarrier { get; init; }

    /// <summary>UTC timestamp the goods were delivered.</summary>
    public DateTime? ShippingDate { get; init; }

    /// <summary>Provider-uploaded receipt / invoice / proof-of-service file IDs.</summary>
    public IReadOnlyList<string>? FileReferences { get; init; }

    /// <summary>
    /// Provider-specific evidence fields not covered by the generic shape above. Use for things like
    /// Stripe's <c>uncategorized_text</c>, Paystack's <c>refund_amount</c>, etc.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ProviderEvidenceFields { get; init; }
}
