# Bhengu.Finance.Payments.ExpressPay

ExpressPay adapter for the Bhengu.Finance.Payments family — hosted-page card and mobile-money checkout across Ghana, Gambia, Sierra Leone, Liberia and Nigeria. Charge, refund (where supported), webhook verification, payouts, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.ExpressPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `ExpressPayPaymentProvider` | Charge (redirect) / webhook verify; refunds not exposed by upstream API |
| `IPayoutProvider` | `ExpressPayPaymentProvider` | Disbursements |
| `ISettlementProvider` | `ExpressPaySettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddExpressPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:ExpressPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "ExpressPay": {
          "MerchantId": "...",
          "ApiKey": "...",
          "RedirectUrl": "https://example.com/expresspay/return",
          "PostUrl": "https://example.com/webhooks/expresspay",
          "Currency": "GHS",
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
    [FromKeyedServices(ProviderNames.ExpressPay)] IPaymentGatewayProvider gateway) : ControllerBase
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
