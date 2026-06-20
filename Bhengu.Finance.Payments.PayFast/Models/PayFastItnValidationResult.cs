// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.PayFast.Models;

/// <summary>
/// Result of the full four-gate PayFast ITN (Instant Transaction Notification) validation performed by
/// <see cref="Providers.PayFastPaymentProvider.ValidateItnAsync"/>.
/// <para>
/// An ITN must only be treated as a settled payment when <see cref="IsValid"/> is <c>true</c>. A valid
/// signature alone is NOT sufficient — PayFast's own server confirmation (gate 3) is what defeats a
/// replayed or forged notification.
/// </para>
/// </summary>
public sealed record PayFastItnValidationResult
{
    /// <summary>
    /// True only when every REQUIRED gate passed: the signature (gate 1) and PayFast's server
    /// confirmation (gate 3), and any OPTIONAL gate that was actually checked — source IP (gate 2)
    /// and amount (gate 4) — did not fail.
    /// </summary>
    public required bool IsValid { get; init; }

    /// <summary>Human-readable reason for the verdict: <c>"valid"</c>, or a description of the first gate that failed.</summary>
    public required string Reason { get; init; }

    /// <summary>Gate 1 — the ITN's MD5 signature matched (computed over the posted fields plus the passphrase).</summary>
    public bool SignatureValid { get; init; }

    /// <summary>
    /// Gate 2 — the POST came from a known PayFast host. <c>null</c> when no source IP was supplied
    /// (the source gate was not run and does not affect <see cref="IsValid"/>).
    /// </summary>
    public bool? SourceValid { get; init; }

    /// <summary>Gate 3 — PayFast's server confirmed the ITN as <c>VALID</c> via <c>/eng/query/validate</c>.</summary>
    public bool ServerConfirmed { get; init; }

    /// <summary>
    /// Gate 4 — <c>amount_gross</c> matched the caller-supplied expected amount. <c>null</c> when no
    /// expected amount was supplied (the amount gate was not run and does not affect <see cref="IsValid"/>).
    /// </summary>
    public bool? AmountMatched { get; init; }

    /// <summary>PayFast's payment id (<c>pf_payment_id</c>) from the ITN, if present.</summary>
    public string? PfPaymentId { get; init; }

    /// <summary>The merchant payment id (<c>m_payment_id</c>) echoed back in the ITN, if present.</summary>
    public string? MPaymentId { get; init; }

    /// <summary>The ITN <c>payment_status</c> (e.g. COMPLETE / FAILED / PENDING / CANCELLED), if present.</summary>
    public string? PaymentStatus { get; init; }

    /// <summary>The gross amount reported by the ITN (<c>amount_gross</c>); 0 when absent or unparseable.</summary>
    public decimal AmountGross { get; init; }
}
