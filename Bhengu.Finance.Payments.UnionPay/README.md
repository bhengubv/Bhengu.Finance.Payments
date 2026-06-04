# Bhengu.Finance.Payments.UnionPay

UnionPay International adapter for the Bhengu.Finance.Payments family — global merchant API for accepting UnionPay cards (the world's largest card scheme by issuance) and generating UnionPay QR codes. Charge, webhook verification, 3-D Secure step-up, and settlement reconciliation behind the Bhengu canonical contracts. RSA signed.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.UnionPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `UnionPayPaymentProvider` | Charge / webhook verify |
| `IThreeDSecureProvider` | `UnionPayThreeDSecureProvider` | SCA step-up flow |
| `IQrCodeProvider` | `UnionPayQrCodeProvider` | UnionPay QR-code generation |
| `ISettlementProvider` | `UnionPaySettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddUnionPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:UnionPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "UnionPay": {
          "MerId": "...",
          "CertId": "...",
          "SignCertPrivateKey": "MIIE...",
          "VerifyCertPublicKey": "MIIB...",
          "FrontUrl": "https://example.com/unionpay/return",
          "BackUrl": "https://example.com/webhooks/unionpay",
          "Currency": "156",                // numeric ISO-4217 — 156 = CNY
          "Encoding": "UTF-8",
          "UseSandbox": false,
          "BaseUrl": null,        // optional override
          "SandboxUrl": null      // optional override
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
    [FromKeyedServices(ProviderNames.UnionPay)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

## Capabilities at runtime

```csharp
if (gateway is IThreeDSecureProvider tds)
    var challenge = await tds.StartAuthenticationAsync(intent);

if (gateway is IQrCodeProvider qr)
    var image = await qr.GenerateAsync(qrRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
