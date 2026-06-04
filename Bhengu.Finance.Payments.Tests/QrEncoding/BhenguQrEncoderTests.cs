// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.QrEncoding;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.QrEncoding;

/// <summary>
/// Smoke tests covering the QrEncoding wrapper: PNG byte output is a valid PNG, SVG output contains
/// an <c>&lt;svg</c> root, UpgradeToImage round-trips a payload QR into an image QR preserving
/// metadata, and the upgrade throws when the source format isn't Payload.
/// </summary>
public class BhenguQrEncoderTests
{
    private const string Payload = "00020101021226580014A000000615000101021202A0123456789012340303123040502000305COM4006FOO BAR5303710540510.005802ZA5912Test Merchant6304ABCD";

    [Fact]
    public void EncodePng_ReturnsNonEmptyPngBytes()
    {
        var bytes = BhenguQrEncoder.EncodePng(Payload);
        Assert.NotEmpty(bytes);
        // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]); // 'P'
        Assert.Equal(0x4E, bytes[2]); // 'N'
        Assert.Equal(0x47, bytes[3]); // 'G'
    }

    [Fact]
    public void EncodeSvg_ReturnsSvgMarkupString()
    {
        var svg = BhenguQrEncoder.EncodeSvg(Payload);
        Assert.False(string.IsNullOrEmpty(svg));
        Assert.Contains("<svg", svg, StringComparison.Ordinal);
        Assert.Contains("</svg>", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeToImage_FromPayloadToPng_PopulatesImageBytesAndDropsPayload()
    {
        var source = new QrCode
        {
            Reference = "payshap_ref_1",
            Format = QrFormat.Payload,
            Payload = Payload,
            Amount = 100m,
            Currency = "ZAR",
        };

        var upgraded = BhenguQrEncoder.UpgradeToImage(source, QrFormat.Png);

        Assert.Equal(QrFormat.Png, upgraded.Format);
        Assert.NotNull(upgraded.ImageBytes);
        Assert.NotEmpty(upgraded.ImageBytes!);
        Assert.Null(upgraded.Payload);
        // Metadata preserved
        Assert.Equal("payshap_ref_1", upgraded.Reference);
        Assert.Equal(100m, upgraded.Amount);
        Assert.Equal("ZAR", upgraded.Currency);
    }

    [Fact]
    public void UpgradeToImage_FromPayloadToSvg_PopulatesPayloadWithSvgMarkup()
    {
        var source = new QrCode
        {
            Reference = "ref",
            Format = QrFormat.Payload,
            Payload = Payload,
            Currency = "ZAR",
        };

        var upgraded = BhenguQrEncoder.UpgradeToImage(source, QrFormat.Svg);

        Assert.Equal(QrFormat.Svg, upgraded.Format);
        Assert.NotNull(upgraded.Payload);
        Assert.Contains("<svg", upgraded.Payload!, StringComparison.Ordinal);
        Assert.Null(upgraded.ImageBytes);
    }

    [Fact]
    public void UpgradeToImage_Throws_WhenSourceFormatIsNotPayload()
    {
        var qr = new QrCode
        {
            Reference = "ref",
            Format = QrFormat.Png,
            ImageBytes = Encoding.UTF8.GetBytes("not a real png"),
            Currency = "ZAR",
        };
        Assert.Throws<ArgumentException>(() => BhenguQrEncoder.UpgradeToImage(qr, QrFormat.Svg));
    }

    [Fact]
    public void UpgradeToImage_Throws_WhenPayloadIsNull()
    {
        var qr = new QrCode
        {
            Reference = "ref",
            Format = QrFormat.Payload,
            Payload = null,
            Currency = "ZAR",
        };
        Assert.Throws<ArgumentException>(() => BhenguQrEncoder.UpgradeToImage(qr, QrFormat.Png));
    }
}
