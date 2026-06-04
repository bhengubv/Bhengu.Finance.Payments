# Bhengu.Finance.Payments.Alipay

Alipay+ Cross-Border adapter for the Bhengu.Finance.Payments family. Ant Group's global merchant API for accepting Chinese consumers (1B+ wallets), QR-code in-store flows, payouts, and webhook verification — all RSA-SHA256 signed behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Alipay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `AlipayPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `AlipayPaymentProvider` | Wallet payouts via `POST /ams/api/v1/payments/payout` |
| `IQrCodeProvider` | `AlipayQrCodeProvider` | QR-code order generation for in-store / merchant-presented flows |

## Wiring

```csharp
builder.Services.AddAlipayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Alipay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Alipay": {
          "ClientId": "...",
          "MerchantPrivateKey": "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQ...",
          "AlipayPublicKey": "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAr...",
          "NotifyUrl": "https://example.com/webhooks/alipay",
          "RedirectUrl": "https://example.com/alipay/return",
          "Currency": "USD",
          "UseSandbox": true,
          "BaseUrl": null,            // optional override
          "SandboxUrl": null,         // optional override
          "OpenApiGatewayUrl": null   // optional override
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
    [FromKeyedServices(ProviderNames.Alipay)] IPaymentGatewayProvider gateway) : ControllerBase
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
    var image = await qr.GenerateAsync(qrRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
