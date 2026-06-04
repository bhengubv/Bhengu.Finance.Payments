# Bhengu.Finance.Payments.PayJustNow

PayJustNow adapter for the Bhengu.Finance.Payments family — Buy-Now-Pay-Later (3 interest-free instalments over 60 days) for South African consumers. Charge, refund, webhook verification, recurring subscriptions, and debit-order mandates behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayJustNow
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PayJustNowPaymentProvider` | Charge (redirect) / refund / webhook verify |
| `ISubscriptionProvider` | `PayJustNowSubscriptionProvider` | Instalment-plan style recurring schedules |
| `IMandateProvider` | `PayJustNowMandateProvider` | Debit-order / pull-payment mandates |

## Wiring

```csharp
builder.Services.AddPayJustNowPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:PayJustNow`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PayJustNow": {
          "ApiKey": "...",
          "SecretKey": "...",
          "MerchantId": "...",
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
    [FromKeyedServices(ProviderNames.PayJustNow)] IPaymentGatewayProvider gateway) : ControllerBase
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
