# Bhengu.Finance.Payments.CMI

CMI (Centre Monétique Interbancaire) adapter for the Bhengu.Finance.Payments family — Morocco's national 3-D Secure card-acquiring gateway based on the Garanti BBVA POS XML protocol. Redirect-only checkout with charge, refund, webhook verification, 3-D Secure step-up flow, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.CMI
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `CMIPaymentProvider` | Charge (redirect) / refund / webhook verify |
| `IThreeDSecureProvider` | `CMIThreeDSecureProvider` | 3-D Secure step-up flow |
| `ISettlementProvider` | `CMISettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddCMIPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:CMI`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "CMI": {
          "ClientId": "...",
          "StoreKey": "...",
          "ApiUser": "...",
          "ApiPassword": "...",
          "OkUrl": "https://example.com/cmi/ok",
          "FailUrl": "https://example.com/cmi/fail",
          "CallbackUrl": "https://example.com/webhooks/cmi",
          "Currency": "504",          // numeric ISO-4217 — 504 = MAD
          "Lang": "en",
          "UseSandbox": true,
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
    [FromKeyedServices(ProviderNames.CMI)] IPaymentGatewayProvider gateway) : ControllerBase
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
