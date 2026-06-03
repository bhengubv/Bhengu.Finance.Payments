// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.QrCode;

/// <summary>
/// How the consumer wants the QR returned.
/// </summary>
public enum QrFormat
{
    /// <summary>Raw payload string the consumer renders into a QR themselves (small / fast / no image deps).</summary>
    Payload,

    /// <summary>PNG image bytes.</summary>
    Png,

    /// <summary>SVG markup as a UTF-8 string.</summary>
    Svg
}
