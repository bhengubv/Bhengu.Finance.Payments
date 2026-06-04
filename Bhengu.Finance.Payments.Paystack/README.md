# Bhengu.Finance.Payments.Paystack

Paystack adapter for the Bhengu.Finance.Payments family. Server-to-server card charges, transfers (payouts), and refunds across Nigeria, Ghana, South Africa, Kenya, Côte d'Ivoire, and Egypt via the Paystack REST API. Charge, refund, webhook verification, payouts, vaulted tokenisation, recurring subscriptions, dispute lifecycle, marketplace splits, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Paystack
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PaystackPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `PaystackPaymentProvider` | Transfers via `POST transfer` |
| `IPayoutProvider` | `PaystackPayoutProvider` | Standalone payout adapter |
| `ITokenisationProvider` | `PaystackTokenisationProvider` | Read vaulted authorization codes |
| `ISubscriptionProvider` | `PaystackSubscriptionProvider` | Plans + subscriptions |
| `IDisputeProvider` | `PaystackDisputeProvider` | Chargeback lifecycle |
| `IMarketplaceProvider` | `PaystackMarketplaceProvider` | Split payments + sub-accounts |
| `ISettlementProvider` | `PaystackSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddPaystackPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Paystack`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Paystack": {
          "SecretKey": "sk_test_...",
          "WebhookSecret": "...",
          "DefaultEmail": "noreply@example.com",   // optional
          "BaseUrl": null                           // optional override
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
    [FromKeyedServices(ProviderNames.Paystack)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is a Paystack `authorization_code`
(typically `AUTH_...`) from a prior tokenisation.

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Refund))
    await gateway.ProcessRefundAsync(refundRequest);

if (gateway is IMarketplaceProvider marketplace)
    var split = await marketplace.CreateSplitAsync(splitRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
