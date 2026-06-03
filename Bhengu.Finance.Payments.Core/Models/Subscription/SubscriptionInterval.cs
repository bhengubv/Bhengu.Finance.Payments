// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Subscription;

/// <summary>
/// Recurring billing interval.
/// </summary>
public enum SubscriptionInterval
{
    /// <summary>Charge daily.</summary>
    Daily,

    /// <summary>Charge weekly.</summary>
    Weekly,

    /// <summary>Charge every two weeks.</summary>
    BiWeekly,

    /// <summary>Charge monthly.</summary>
    Monthly,

    /// <summary>Charge every three months.</summary>
    Quarterly,

    /// <summary>Charge every six months.</summary>
    BiAnnually,

    /// <summary>Charge yearly.</summary>
    Annually
}
