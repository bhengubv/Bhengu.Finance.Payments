# Bhengu.Finance.Payments.WeChatPay

WeChat Pay adapter for the Bhengu.Finance.Payments family — Tencent's global merchant API for accepting WeChat consumers (1.3B+ monthly actives) via Native QR, JSAPI, MiniProgram, App, and H5 flows. Charge, refund, webhook verification, payouts, and Native QR generation behind the Bhengu canonical contracts. WeChat Pay v3 (SHA-256 + RSA / AES-GCM).

## Install

```sh
dotnet add package Bhengu.Finance.Payments.WeChatPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `WeChatPayPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `WeChatPayPaymentProvider` | Merchant transfers |
| `IQrCodeProvider` | `WeChatPayQrCodeProvider` | Native QR-code generation |

## Wiring

```csharp
builder.Services.AddWeChatPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:WeChatPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "WeChatPay": {
          "AppId": "wx...",
          "MerchantId": "...",
          "MerchantCertSerialNo": "...",
          "MerchantPrivateKey": "MIIE...",
          "V3ApiKey": "...",                                // 32-byte API v3 key for AES-GCM
          "WeChatPayPlatformCertificate": "MIIB...",
          "NotifyUrl": "https://example.com/webhooks/wechatpay",
          "Currency": "CNY",
          "UseSandbox": false,
          "BaseUrl": null         // optional override
        }
      }
    }
  }
}
```

## Usage

```csharp
[ApiController]
public class CheckoutController(
    [FromKeyedServices(ProviderNames.WeChatPay)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Refund))
    await gateway.ProcessRefundAsync(refundRequest);

if (gateway is IQrCodeProvider qr)
    var native = await qr.GenerateAsync(qrRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
