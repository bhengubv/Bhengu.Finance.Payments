# Bhengu.Finance.Payments.Yoco

Yoco adapter for the Bhengu.Finance.Payments family — South Africa's largest local-merchant card acquirer. Server-to-server card charges, refunds, payouts, tokenisation (vaulted + SAQ-D raw card), 3-D Secure step-up, and settlement reconciliation via Yoco's Online REST API, behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Yoco
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `YocoPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `YocoPayoutProvider` | Merchant payouts |
| `ITokenisationProvider` | `YocoTokenisationProvider` | Read vaulted card tokens |
| `IRawCardTokenisationProvider` | `YocoRawCardTokenisationProvider` | SAQ-D — accepts raw PAN+CVV |
| `IThreeDSecureProvider` | `YocoThreeDSecureProvider` | SCA step-up flow |
| `ISettlementProvider` | `YocoSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddYocoPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Yoco`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Yoco": {
          "SecretKey": "sk_test_...",
          "WebhookSecret": "whsec_...",
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
    [FromKeyedServices(ProviderNames.Yoco)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is a Yoco card token (e.g. `tok_visa_...`)
from client-side tokenisation in Yoco's Web/Mobile SDK.

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
