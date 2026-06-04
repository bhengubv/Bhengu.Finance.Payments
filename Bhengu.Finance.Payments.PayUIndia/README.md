# Bhengu.Finance.Payments.PayUIndia

PayU India adapter for the Bhengu.Finance.Payments family — cards, UPI, netbanking, EMI, and BNPL via the PayU India hosted-page redirect plus the info-service JSON API. Charge, refund, webhook verification, payouts, vaulted card tokenisation, 3-D Secure step-up, recurring subscriptions, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayUIndia
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PayUIndiaPaymentProvider` | Charge (redirect) / refund / webhook verify |
| `IPayoutProvider` | `PayUIndiaPaymentProvider` | Fund transfers via the info-service |
| `ITokenisationProvider` | `PayUIndiaTokenisationProvider` | Read vaulted card tokens |
| `IThreeDSecureProvider` | `PayUIndiaThreeDSecureProvider` | SCA step-up flow |
| `ISubscriptionProvider` | `PayUIndiaSubscriptionProvider` | Plans + recurring billing |
| `ISettlementProvider` | `PayUIndiaSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddPayUIndiaPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:PayUIndia`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PayUIndia": {
          "MerchantKey": "...",
          "Salt": "...",
          "SuccessUrl": "https://example.com/payu/success",
          "FailureUrl": "https://example.com/payu/failure",
          "Currency": "INR",
          "UseSandbox": true,
          "BaseUrl": null,        // optional override
          "SandboxUrl": null,     // optional override
          "InfoBaseUrl": null     // optional override
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
    [FromKeyedServices(ProviderNames.PayUIndia)] IPaymentGatewayProvider gateway) : ControllerBase
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
