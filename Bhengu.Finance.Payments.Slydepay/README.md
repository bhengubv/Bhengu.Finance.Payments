# Bhengu.Finance.Payments.Slydepay

Slydepay adapter for the Bhengu.Finance.Payments family — mobile-first wallet checkout for Ghana via the legacy `paymentservice.asmx` JSON API. Charge and webhook verification behind the Bhengu canonical contracts; refunds are handled via the merchant portal.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Slydepay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `SlydepayPaymentProvider` | Charge (redirect) / webhook verify; refunds via merchant portal |

## Wiring

```csharp
builder.Services.AddSlydepayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Slydepay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Slydepay": {
          "EmailOrMobile": "merchant@example.com",
          "MerchantKey": "...",
          "Currency": "GHS",
          "PaymentChannels": "7",                                 // 1 card, 2 mobile, 4 wallet, 7 all
          "CallbackUrl": "https://example.com/webhooks/slydepay",
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
    [FromKeyedServices(ProviderNames.Slydepay)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Webhook))
    var evt = await gateway.ParseWebhookAsync(body);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
