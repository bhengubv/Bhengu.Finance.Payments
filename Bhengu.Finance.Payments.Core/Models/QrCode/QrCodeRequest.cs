// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.QrCode;

/// <summary>
/// A request to generate a payment QR.
/// </summary>
public sealed record QrCodeRequest
{
    /// <summary>
    /// Amount to charge in the major currency unit. Null produces a STATIC QR (payer enters the amount
    /// on their wallet); a value produces a DYNAMIC QR locked to that amount.
    /// </summary>
    public decimal? Amount { get; init; }

    /// <summary>ISO 4217 currency code. Required even for static QRs that don't lock an amount.</summary>
    public required string Currency { get; init; }

    /// <summary>Human-readable description shown on the payer's wallet.</summary>
    public required string Description { get; init; }

    /// <summary>Merchant reference for reconciliation. Echoed back on the webhook.</summary>
    public required string MerchantReference { get; init; }

    /// <summary>Desired return format.</summary>
    public QrFormat Format { get; init; } = QrFormat.Payload;

    /// <summary>
    /// UTC expiry. Null means "provider default" (typically 1-15 minutes for dynamic QRs).
    /// Static QRs ignore this.
    /// </summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Optional payer identifier when the provider needs it (e.g. Alipay buyer_id).</summary>
    public string? PayerIdentifier { get; init; }

    /// <summary>Caller-supplied idempotency key.</summary>
    public string? IdempotencyKey { get; init; }
}
