# Bhengu.Finance.Payments.MercadoPago

Mercado Pago adapter for the Bhengu.Finance.Payments family — Argentina, Brazil, Chile, Colombia, Mexico, Peru, Uruguay and Venezuela. Cards, PIX, boleto, and Mercado wallet via the Mercado Pago REST API, with charge, refund, webhook verification, payouts, vaulted tokenisation, recurring subscriptions, marketplace splits, and PIX QR generation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.MercadoPago
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `MercadoPagoPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `MercadoPagoPaymentProvider` | Money-request disbursement |
| `ITokenisationProvider` | `MercadoPagoTokenisationProvider` | Read vaulted card tokens |
| `ISubscriptionProvider` | `MercadoPagoSubscriptionProvider` | Plans + subscriptions |
| `IMarketplaceProvider` | `MercadoPagoMarketplaceProvider` | Split payments + sub-accounts |
| `IQrCodeProvider` | `MercadoPagoQrCodeProvider` | PIX QR-code generation |

## Wiring

```csharp
builder.Services.AddMercadoPagoPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:MercadoPago`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "MercadoPago": {
          "AccessToken": "TEST-...",
          "PublicKey": "TEST-...",                                // optional
          "WebhookSecret": "...",
          "NotificationUrl": "https://example.com/webhooks/mp",   // optional
          "Currency": "BRL",
          "UseSandbox": false,
          "BaseUrl": null                                          // optional override
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
    [FromKeyedServices(ProviderNames.MercadoPago)] IPaymentGatewayProvider gateway) : ControllerBase
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
    var pix = await qr.GenerateAsync(qrRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
