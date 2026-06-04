# Bhengu.Finance.Payments.QrEncoding

A small, dependency-light wrapper over [QRCoder](https://github.com/codebude/QRCoder) (Apache 2.0)
that turns the payload-format `QrCode` returned by the Bhengu Payments SDK's QR providers into
renderable PNG bytes or SVG markup.

## Why this package exists

The QR providers in `Bhengu.Finance.Payments.*` (PayShap, MPesa, Wave, Alipay, WeChat Pay, etc.)
return a `QrCode` whose `Format = QrFormat.Payload` and whose `Payload` is the raw EMVCo / BIP21
string. That's deliberately tiny and image-library-free — most callers only need to ship the
string to a mobile client that already draws the QR itself.

When a caller does need a PNG or SVG (printing a static QR, embedding in a web page, etc.) they
add this package and call one method. No need to take a direct QRCoder dependency or pin its
version against the rest of the SDK.

## Install

```bash
dotnet add package Bhengu.Finance.Payments.QrEncoding
```

## Usage

### Round-trip from a provider

```csharp
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.QrEncoding;

QrCode payloadQr = await qrProvider.GenerateAsync(new QrCodeRequest
{
    Amount = 50m,
    Currency = "ZAR",
    Format = QrFormat.Payload
});

// Upgrade to PNG without re-hitting the provider:
QrCode pngQr = BhenguQrEncoder.UpgradeToImage(payloadQr, QrFormat.Png);
File.WriteAllBytes("payshap.png", pngQr.ImageBytes!);
```

### Direct encoding

```csharp
byte[] png = BhenguQrEncoder.EncodePng("00020101021226...EMVCo...payload...");
string svg = BhenguQrEncoder.EncodeSvg("00020101021226...EMVCo...payload...");
```

## Notes

- Multi-targets `net8.0` and `net10.0`.
- ECC level is `Q` (Quartile, ~25% error correction) which is a good default for printed QRs.
- `pixelsPerModule` default is 10. Raise for larger images, lower for tighter ones.
- `UpgradeToImage` throws `ArgumentException` if the source `QrCode.Format` isn't `Payload`.
  Re-fetch from the provider with `Format=Payload` before upgrading.

## License

Apache 2.0 — same as the rest of the Bhengu Payments SDK.
