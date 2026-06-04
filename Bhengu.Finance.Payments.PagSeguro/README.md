# Bhengu.Finance.Payments.PagSeguro

PagSeguro / PagBank adapter for the Bhengu.Finance.Payments family — Brazil's cards, PIX, boleto, and wallet via the PagBank v4 REST API. Charge, refund, webhook verification, payouts, PIX QR generation, recurring subscriptions, and vaulted card tokenisation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PagSeguro
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PagSeguroPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `PagSeguroPaymentProvider` | Bank transfer disbursement |
| `ITokenisationProvider` | `PagSeguroTokenisationProvider` | Read vaulted card tokens |
| `ISubscriptionProvider` | `PagSeguroSubscriptionProvider` | Plans + subscriptions |
| `IQrCodeProvider` | `PagSeguroQrCodeProvider` | PIX QR-code generation |

## Wiring

```csharp
builder.Services.AddPagSeguroPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:PagSeguro`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PagSeguro": {
          "ApiToken": "...",
          "WebhookSecret": "...",
          "NotificationUrl": "https://example.com/webhooks/pagbank",  // optional
          "Currency": "BRL",
          "UseSandbox": true,
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
    [FromKeyedServices(ProviderNames.PagSeguro)] IPaymentGatewayProvider gateway) : ControllerBase
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
