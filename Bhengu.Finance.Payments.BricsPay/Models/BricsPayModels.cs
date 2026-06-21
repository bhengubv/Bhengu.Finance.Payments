// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.BricsPay.Models;

/// <summary>
/// The state of a BRICS Pay transaction, as returned by the status (<c>GET /ia/get</c>) and refund
/// (<c>POST /ia/refund</c>) endpoints.
/// </summary>
public sealed record BricsPayTransactionStatus
{
    /// <summary>The processor's transaction number (present once <see cref="Processed"/> is true).</summary>
    public string? Transaction { get; init; }

    /// <summary>True once the transaction has been paid.</summary>
    public bool Paid { get; init; }

    /// <summary>True once processing has finished (paid or not).</summary>
    public bool Processed { get; init; }

    /// <summary>Normalised status derived from <see cref="Paid"/> / <see cref="Processed"/>.</summary>
    public PaymentStatus Status { get; init; }

    /// <summary>Transaction amount in the terminal's currency.</summary>
    public decimal Amount { get; init; }

    /// <summary>ISO 4217 numeric currency code reported by the processor.</summary>
    public int CurrencyCode { get; init; }

    /// <summary>Human-readable currency name reported by the processor.</summary>
    public string? CurrencyName { get; init; }

    /// <summary>When the receipt was created (UTC).</summary>
    public DateTime? CreatedUtc { get; init; }

    /// <summary>When the transaction was authorised (UTC), if processed.</summary>
    public DateTime? ProcessedUtc { get; init; }

    /// <summary>When the transaction auto-cancels (UTC).</summary>
    public DateTime? TimeoutUtc { get; init; }

    /// <summary>Authorisation error code, present when <see cref="Paid"/> is false and <see cref="Processed"/> is true.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>Authorisation error message for the user.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// A parsed BRICS Pay callback. NOTE: the captured protocol does not specify how callbacks are signed, so
/// treat this as a signal to confirm the authoritative state via
/// <see cref="Providers.BricsPayPaymentProvider.GetTransactionAsync"/> — do not trust these fields alone.
/// </summary>
public sealed record BricsPayCallback
{
    /// <summary>The terminal ID the callback is for.</summary>
    public string? Pos { get; init; }

    /// <summary>The operation sequence (echoes the merchant reference).</summary>
    public string? Sequence { get; init; }

    /// <summary>The processor's transaction number.</summary>
    public string? Transaction { get; init; }

    /// <summary>True once the transaction has been paid.</summary>
    public bool Paid { get; init; }

    /// <summary>True once processing has finished.</summary>
    public bool Processed { get; init; }

    /// <summary>Normalised status derived from <see cref="Paid"/> / <see cref="Processed"/>.</summary>
    public PaymentStatus Status { get; init; }

    /// <summary>Transaction amount in the terminal's currency.</summary>
    public decimal Amount { get; init; }

    /// <summary>ISO 4217 numeric currency code.</summary>
    public int CurrencyCode { get; init; }
}
