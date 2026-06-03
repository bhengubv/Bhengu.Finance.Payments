// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// Normalised classification of inbound webhook events. Lets consumers route on a stable enum
/// instead of pattern-matching on each provider's <c>EventType</c> string.
/// </summary>
public enum WebhookEventCategory
{
    /// <summary>The event was recognised by the provider adapter but does not map to a known category.</summary>
    Unknown = 0,

    /// <summary>A charge succeeded — funds are committed.</summary>
    ChargeSucceeded,

    /// <summary>A charge failed or was declined.</summary>
    ChargeFailed,

    /// <summary>A charge is pending — provider has accepted but not yet settled.</summary>
    ChargePending,

    /// <summary>A refund succeeded.</summary>
    RefundSucceeded,

    /// <summary>A refund failed.</summary>
    RefundFailed,

    /// <summary>A dispute (chargeback) was opened by the payer.</summary>
    DisputeOpened,

    /// <summary>A dispute was resolved in the merchant's favour.</summary>
    DisputeWon,

    /// <summary>A dispute was resolved in the payer's favour — funds reversed.</summary>
    DisputeLost,

    /// <summary>A subscription was created and is active.</summary>
    SubscriptionCreated,

    /// <summary>A subscription renewed and the recurring charge succeeded.</summary>
    SubscriptionRenewed,

    /// <summary>A subscription was cancelled, paused, or expired.</summary>
    SubscriptionCancelled,

    /// <summary>A subscription's recurring charge failed.</summary>
    SubscriptionChargeFailed,

    /// <summary>A debit-order / pull-payment mandate was activated by the payer.</summary>
    MandateActivated,

    /// <summary>A debit-order mandate was cancelled by the payer or bank.</summary>
    MandateCancelled,

    /// <summary>A payout to a destination completed.</summary>
    PayoutCompleted,

    /// <summary>A payout to a destination failed.</summary>
    PayoutFailed,

    /// <summary>A settlement batch was processed and funds were credited to the merchant account.</summary>
    SettlementCompleted
}
