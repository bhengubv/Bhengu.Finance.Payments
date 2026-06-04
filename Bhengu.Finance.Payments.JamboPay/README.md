# Bhengu.Finance.Payments.JamboPay

JamboPay adapter for the Bhengu.Finance.Payments family — cards, M-Pesa, Airtel Money, and bank rails for Kenya via the JamboPay v1 REST API. Charge, refund, webhook verification, and payouts behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.JamboPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `JamboPayPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `JamboPayPaymentProvider` | Mobile-money or bank disbursement |

## Wiring

```csharp
builder.Services.AddJamboPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:JamboPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "JamboPay": {
          "ApiKey": "...",
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantCode": "...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://example.com/webhooks/jambopay",
          "Currency": "KES",
          "UseSandbox": false,
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
    [FromKeyedServices(ProviderNames.JamboPay)] IPaymentGatewayProvider gateway) : ControllerBase
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
