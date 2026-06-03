// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// The payer opened a dispute (chargeback) against a settled charge. Merchant action is required
/// before <c>EvidenceDueBy</c> or the dispute defaults to the payer's favour.
/// </summary>
public sealed record DisputeOpenedEvent : WebhookEvent
{
    /// <summary>The dispute's gateway reference. Use with <see cref="Interfaces.IDisputeProvider"/> to fetch detail and submit evidence.</summary>
    public required string DisputeReference { get; init; }

    /// <summary>Disputed amount.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Provider-supplied dispute reason code (e.g. "fraudulent", "product_not_received").</summary>
    public string? ReasonCode { get; init; }

    /// <summary>UTC deadline for submitting evidence. After this date the dispute auto-resolves in the payer's favour.</summary>
    public DateTime? EvidenceDueBy { get; init; }
}
