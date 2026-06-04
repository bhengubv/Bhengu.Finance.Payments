// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models.QrCode;
using QRCoder;

namespace Bhengu.Finance.Payments.QrEncoding;

/// <summary>
/// Thin wrapper over <see href="https://github.com/codebude/QRCoder">QRCoder</see> that turns the
/// payload-format <see cref="QrCode"/> returned by the SDK's QR providers into renderable PNG bytes
/// or SVG markup. Consumers depend on this package so they don't have to take QRCoder directly and
/// can swap the encoder later without touching provider integration code.
///
/// <para>The QR providers return <see cref="QrFormat.Payload"/> by default — calling
/// <see cref="UpgradeToImage"/> converts the payload into the requested image format in-place.</para>
/// </summary>
public static class BhenguQrEncoder
{
    /// <summary>Default modules-per-pixel used when no explicit value is supplied.</summary>
    public const int DefaultPixelsPerModule = 10;

    /// <summary>
    /// Encode <paramref name="payload"/> as a PNG byte stream.
    /// </summary>
    /// <param name="payload">UTF-8 payload string (typically an EMVCo QR string or BIP21-style URI).</param>
    /// <param name="pixelsPerModule">Pixels per QR module — larger values produce bigger images. Defaults to 10.</param>
    /// <returns>PNG-encoded byte array suitable for serving as <c>image/png</c>.</returns>
    public static byte[] EncodePng(string payload, int pixelsPerModule = DefaultPixelsPerModule)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelsPerModule);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        using var png = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Encode <paramref name="payload"/> as an SVG-markup string.
    /// </summary>
    /// <param name="payload">UTF-8 payload string.</param>
    /// <param name="pixelsPerModule">Pixels per QR module. Defaults to 10.</param>
    /// <returns>SVG markup suitable for serving as <c>image/svg+xml</c> or embedding inline.</returns>
    public static string EncodeSvg(string payload, int pixelsPerModule = DefaultPixelsPerModule)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelsPerModule);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var svg = new SvgQRCode(data);
        return svg.GetGraphic(pixelsPerModule);
    }

    /// <summary>
    /// Convenience: take a payload-format <see cref="QrCode"/> as returned by an
    /// <see cref="Bhengu.Finance.Payments.Core.Interfaces.IQrCodeProvider"/> and produce a new
    /// <see cref="QrCode"/> whose <see cref="QrCode.Format"/> matches <paramref name="format"/>
    /// with <see cref="QrCode.ImageBytes"/> or <see cref="QrCode.Payload"/> populated accordingly.
    /// </summary>
    /// <param name="qr">The source QR. <see cref="QrCode.Format"/> MUST be <see cref="QrFormat.Payload"/>.</param>
    /// <param name="format">The desired output format. <see cref="QrFormat.Payload"/> is a no-op (returns <paramref name="qr"/>).</param>
    /// <returns>A new <see cref="QrCode"/> with the requested format. All other properties (Reference, Amount, Currency, ExpiresAt) are preserved.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="qr"/> was null.</exception>
    /// <exception cref="ArgumentException">The source <see cref="QrCode.Format"/> wasn't <see cref="QrFormat.Payload"/>, or <see cref="QrCode.Payload"/> was null.</exception>
    public static QrCode UpgradeToImage(QrCode qr, QrFormat format)
    {
        ArgumentNullException.ThrowIfNull(qr);
        if (qr.Format != QrFormat.Payload)
            throw new ArgumentException(
                $"UpgradeToImage requires the source QrCode.Format to be Payload (was {qr.Format}). Re-fetch the QR from the provider with Format=Payload before upgrading.",
                nameof(qr));
        if (string.IsNullOrEmpty(qr.Payload))
            throw new ArgumentException("QrCode.Payload was null or empty; nothing to encode.", nameof(qr));

        return format switch
        {
            QrFormat.Payload => qr,
            QrFormat.Png => qr with
            {
                Format = QrFormat.Png,
                ImageBytes = EncodePng(qr.Payload),
                Payload = null
            },
            QrFormat.Svg => qr with
            {
                Format = QrFormat.Svg,
                Payload = EncodeSvg(qr.Payload),
                ImageBytes = null
            },
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown QrFormat.")
        };
    }
}
