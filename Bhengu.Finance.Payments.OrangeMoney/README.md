# Bhengu.Finance.Payments.OrangeMoney

Orange Money Web Payment adapter for the Bhengu.Finance.Payments family. Hosted-checkout redirect flow across Côte d'Ivoire, Senegal, Cameroon, Mali, Burkina Faso, Madagascar, Niger, Botswana, Sierra Leone, Guinea (Conakry & Bissau), Liberia, and the DRC — with charge, webhook verification, and payouts behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.OrangeMoney
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `OrangeMoneyPaymentProvider` | Charge (redirect) / webhook verify; no automated refund API |
| `IPayoutProvider` | `OrangeMoneyPaymentProvider` | Wallet disbursement |

## Wiring

```csharp
builder.Services.AddOrangeMoneyPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:OrangeMoney`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "OrangeMoney": {
          "ConsumerKey": "...",
          "ConsumerSecret": "...",
          "MerchantKey": "...",
          "Country": "ci",                                       // ci, sn, cm, ml, ...
          "ReturnUrl": "https://example.com/orange/return",
          "CancelUrl": "https://example.com/orange/cancel",
          "NotifUrl": "https://example.com/orange/notif",
          "UseSandbox": true,
          "BaseUrl": null,           // optional override
          "SandboxUrl": null         // optional override
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
    [FromKeyedServices(ProviderNames.OrangeMoney)] IPaymentGatewayProvider gateway) : ControllerBase
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
