# Bhengu.Finance.Payments.BricsPay

BricsPay adapter for the Bhengu.Finance.Payments family — Bhengu B.V.'s in-house BRICS-bloc payment rail (Brazil, Russia, India, China, South Africa plus the 2024 expansion members). Charge, refund, webhook verification, payouts, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.BricsPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `BricsPayPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `BricsPayPaymentProvider` | Wallet payouts |
| `ISettlementProvider` | `BricsPaySettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddBricsPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:BricsPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "BricsPay": {
          "MerchantId": "...",
          "SecretKey": "...",
          "WebhookSecret": "...",
          "UseSandbox": false,
          "BaseUrl": null,     // optional override
          "SandboxUrl": null   // optional override
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
    [FromKeyedServices(ProviderNames.BricsPay)] IPaymentGatewayProvider gateway) : ControllerBase
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
