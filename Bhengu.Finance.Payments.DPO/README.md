# Bhengu.Finance.Payments.DPO

DPO Group (Network International) adapter for the Bhengu.Finance.Payments family. Pan-African card gateway covering 20+ countries via the DPO v6 Direct API — redirect-flow checkout, charge, refund, webhook verification, payouts where supported by the merchant tier, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.DPO
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `DPOPaymentProvider` | Charge (redirect) / refund / webhook verify |
| `IPayoutProvider` | `DPOPaymentProvider` | Disbursements where merchant tier allows |
| `ISettlementProvider` | `DPOSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddDPOPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:DPO`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "DPO": {
          "CompanyToken": "...",
          "ServiceType": "3854",
          "ServiceDescription": "Online order",            // optional
          "RedirectUrl": "https://example.com/dpo/return", // optional
          "BackUrl": "https://example.com/dpo/cancel",     // optional
          "UseSandbox": true,
          "BaseUrl": null                                   // optional override
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
    [FromKeyedServices(ProviderNames.DPO)] IPaymentGatewayProvider gateway) : ControllerBase
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
