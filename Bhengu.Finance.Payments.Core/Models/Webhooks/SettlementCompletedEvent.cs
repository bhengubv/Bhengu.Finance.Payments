// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A settlement batch was processed and funds were credited to the merchant's bank account.
/// Use <see cref="Interfaces.ISettlementProvider.GetSettlementAsync"/> to enumerate the constituent
/// transactions for reconciliation.
/// </summary>
public sealed record SettlementCompletedEvent : WebhookEvent
{
    /// <summary>The settlement batch's gateway reference.</summary>
    public required string SettlementReference { get; init; }

    /// <summary>Total amount credited to the merchant after fees.</summary>
    public required decimal NetAmount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }

    /// <summary>Gross amount before fees.</summary>
    public decimal? GrossAmount { get; init; }

    /// <summary>Fees deducted by the provider.</summary>
    public decimal? Fees { get; init; }

    /// <summary>Count of transactions in the batch.</summary>
    public int? TransactionCount { get; init; }
}
