# Bhengu.Finance.Payments.Fawry

Fawry adapter for the Bhengu.Finance.Payments family — Egypt's largest payment network covering PayAtFawry retail outlets, MWALLET, and cards via the Fawry ECommerce REST API. Charge, refund, webhook verification, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Fawry
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `FawryPaymentProvider` | Charge / refund / webhook verify |
| `ISettlementProvider` | `FawrySettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddFawryPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Fawry`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Fawry": {
          "MerchantCode": "...",
          "SecurityKey": "...",
          "DefaultPaymentMethod": "CARD",
          "ReturnUrl": "https://example.com/fawry/return",         // optional
          "NotificationUrl": "https://example.com/webhooks/fawry", // optional
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
    [FromKeyedServices(ProviderNames.Fawry)] IPaymentGatewayProvider gateway) : ControllerBase
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
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
