# Bhengu.Finance.Payments.Stripe

Stripe payment-gateway adapter for the Bhengu.Finance.Payments family. Global card payments, payouts via Stripe Connect, subscriptions (with pause/resume), 3-D Secure step-up, dispute lifecycle, settlement reconciliation, SEPA/BACS mandates, and Connect-based marketplace splits — all behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Stripe
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `StripePaymentProvider` | Charge / refund / webhook verify via PaymentIntents |
| `IPayoutProvider` | `StripePaymentProvider` | Payouts via `PayoutService` (Connect / platform balance) |
| `ITokenisationProvider` | `StripeTokenisationProvider` | Read vaulted PaymentMethods (`pm_xxx`) |
| `ISubscriptionProvider` | `StripeSubscriptionProvider` | Plans (Prices) + subscriptions |
| `ISubscriptionPauseSupport` | `StripeSubscriptionProvider` | Pause / resume via `pause_collection` |
| `IThreeDSecureProvider` | `StripeThreeDSecureProvider` | SCA step-up flow |
| `IDisputeProvider` | `StripeDisputeProvider` | Chargeback lifecycle |
| `ISettlementProvider` | `StripeSettlementProvider` | Reconciliation feed via balance transactions |
| `IMandateProvider` | `StripeMandateProvider` | Debit-order / pull-payment (SEPA, BACS, ACH) |
| `IMarketplaceProvider` | `StripeMarketplaceProvider` | Stripe Connect: split payments + sub-accounts |

## Wiring

```csharp
builder.Services.AddStripePayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Stripe`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Stripe": {
          "SecretKey": "sk_test_...",
          "WebhookSecret": "whsec_...",
          "ConnectClientId": "ca_..."   // optional, required for Standard Connect OAuth links
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
    [FromKeyedServices(ProviderNames.Stripe)] IPaymentGatewayProvider gateway) : ControllerBase
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
