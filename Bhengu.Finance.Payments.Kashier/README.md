# Bhengu.Finance.Payments.Kashier

Kashier adapter for the Bhengu.Finance.Payments family — server-to-server card charges, marketplace payouts, and hosted-payment-page redirect for Egypt, the UAE and KSA via the Kashier REST API. Charge, refund, webhook verification, payouts, vaulted tokenisation, and 3-D Secure step-up behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Kashier
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `KashierPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `KashierPaymentProvider` | Marketplace payouts |
| `ITokenisationProvider` | `KashierTokenisationProvider` | Read vaulted card tokens |
| `IThreeDSecureProvider` | `KashierThreeDSecureProvider` | SCA step-up flow |

## Wiring

```csharp
builder.Services.AddKashierPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Kashier`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Kashier": {
          "ApiKey": "...",
          "MerchantId": "MID-...",
          "SecretKey": "...",
          "WebhookSecret": "...",
          "Currency": "EGP",
          "Mode": "test",
          "RedirectUrl": "https://example.com/kashier/return",          // optional
          "ServerWebhookUrl": "https://example.com/webhooks/kashier",   // optional
          "UseSandbox": true,
          "BaseUrl": null                                                // optional override
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
    [FromKeyedServices(ProviderNames.Kashier)] IPaymentGatewayProvider gateway) : ControllerBase
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

if (gateway is IThreeDSecureProvider tds)
    var challenge = await tds.StartAuthenticationAsync(intent);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
