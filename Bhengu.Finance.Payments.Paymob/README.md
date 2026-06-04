# Bhengu.Finance.Payments.Paymob

Paymob adapter for the Bhengu.Finance.Payments family — hosted-iframe checkout and disbursements across Egypt, the GCC, and Pakistan via Paymob Accept's 4-step auth-handshake REST API. Charge, refund, webhook verification, payouts, vaulted card tokenisation, 3-D Secure step-up, recurring subscriptions, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Paymob
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PaymobPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `PaymobPaymentProvider` | Disbursement transactions |
| `ITokenisationProvider` | `PaymobTokenisationProvider` | Read vaulted card tokens |
| `IThreeDSecureProvider` | `PaymobThreeDSecureProvider` | SCA step-up flow |
| `ISubscriptionProvider` | `PaymobSubscriptionProvider` | Plans + subscriptions |
| `ISettlementProvider` | `PaymobSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddPaymobPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Paymob`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Paymob": {
          "ApiKey": "...",
          "HmacSecret": "...",
          "IntegrationId": 123456,
          "IframeId": 78910,
          "Currency": "EGP",
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
    [FromKeyedServices(ProviderNames.Paymob)] IPaymentGatewayProvider gateway) : ControllerBase
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
