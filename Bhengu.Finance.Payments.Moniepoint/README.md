# Bhengu.Finance.Payments.Moniepoint

Moniepoint adapter for the Bhengu.Finance.Payments family — Nigeria's largest agent-banking network. Hosted checkout, refunds, inter-bank transfers, and settlement reconciliation via the Moniepoint REST API behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Moniepoint
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `MoniepointPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `MoniepointPaymentProvider` | Inter-bank transfers |
| `ISettlementProvider` | `MoniepointSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddMoniepointPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Moniepoint`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Moniepoint": {
          "ApiKey": "mpt_...",
          "WebhookSecret": "...",
          "MerchantId": "...",
          "RedirectUrl": "https://example.com/moniepoint/return",
          "UseSandbox": true,
          "BaseUrl": null         // optional override
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
    [FromKeyedServices(ProviderNames.Moniepoint)] IPaymentGatewayProvider gateway) : ControllerBase
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
