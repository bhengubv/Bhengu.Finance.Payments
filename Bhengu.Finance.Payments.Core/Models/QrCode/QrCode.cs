// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.QrCode;

/// <summary>
/// The generated QR — either a raw payload, a PNG, or an SVG.
/// </summary>
public sealed record QrCode
{
    /// <summary>The provider's QR / order reference. Use to query payment status via the parent provider.</summary>
    public required string Reference { get; init; }

    /// <summary>The QR data, encoded per <see cref="Format"/>.</summary>
    public required QrFormat Format { get; init; }

    /// <summary>UTF-8 string payload (for <see cref="QrFormat.Payload"/> or <see cref="QrFormat.Svg"/>). Null for Png.</summary>
    public string? Payload { get; init; }

    /// <summary>Raw bytes (for <see cref="QrFormat.Png"/>). Null for Payload / Svg.</summary>
    public byte[]? ImageBytes { get; init; }

    /// <summary>UTC timestamp the QR will stop being honoured. Null for static QRs that don't expire.</summary>
    public DateTime? ExpiresAt { get; init; }

    /// <summary>Amount locked on the QR (if dynamic) or null (if static).</summary>
    public decimal? Amount { get; init; }

    /// <summary>ISO 4217 currency code.</summary>
    public required string Currency { get; init; }
}
