# Bhengu.Finance.Payments.IPay

iPay (Africa) adapter for the Bhengu.Finance.Payments family — Kenya-centric pan-African gateway covering Kenya, Uganda, Tanzania, Rwanda and the DRC. Cards, M-Pesa, Airtel Money, Equitel and bank rails via iPay v3, with charge, webhook verification, payouts, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.IPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `IPayPaymentProvider` | Charge (redirect) / webhook verify; refunds via merchant portal only |
| `IPayoutProvider` | `IPayPaymentProvider` | Disbursements |
| `ISettlementProvider` | `IPaySettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddIPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:IPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "IPay": {
          "VendorId": "demo",
          "HashKey": "...",
          "Live": "1",                // "1" = live, "0" = test
          "Currency": "KES",
          "CallbackUrl": "https://example.com/webhooks/ipay",
          "UseSandbox": false,
          "BaseUrl": null,            // optional override
          "SandboxUrl": null          // optional override
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
    [FromKeyedServices(ProviderNames.IPay)] IPaymentGatewayProvider gateway) : ControllerBase
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
