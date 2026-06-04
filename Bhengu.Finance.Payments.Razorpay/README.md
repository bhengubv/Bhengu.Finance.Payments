# Bhengu.Finance.Payments.Razorpay

Razorpay adapter for the Bhengu.Finance.Payments family — India's most popular gateway covering cards, UPI, netbanking, EMI, and wallets via the Razorpay REST API. Server-side capture, refunds, RazorpayX payouts, vaulted card tokenisation, 3-D Secure step-up, recurring subscriptions, mandates (eNACH/UPI AutoPay), dispute lifecycle, marketplace Route splits, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Razorpay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `RazorpayPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `RazorpayPaymentProvider` | RazorpayX IMPS payouts |
| `IPayoutProvider` | `RazorpayPayoutProvider` | Standalone payout adapter |
| `ITokenisationProvider` | `RazorpayTokenisationProvider` | Read vaulted card tokens |
| `IThreeDSecureProvider` | `RazorpayThreeDSecureProvider` | SCA step-up flow |
| `ISubscriptionProvider` | `RazorpaySubscriptionProvider` | Plans + subscriptions |
| `IMandateProvider` | `RazorpayMandateProvider` | eNACH / UPI AutoPay mandates |
| `IDisputeProvider` | `RazorpayDisputeProvider` | Chargeback lifecycle |
| `IMarketplaceProvider` | `RazorpayMarketplaceProvider` | Route split payments + sub-accounts |
| `ISettlementProvider` | `RazorpaySettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddRazorpayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Razorpay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Razorpay": {
          "KeyId": "rzp_test_...",
          "KeySecret": "...",
          "WebhookSecret": "...",
          "RazorpayXAccountNumber": "2323230012345678",   // required for payouts
          "Currency": "INR",
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
    [FromKeyedServices(ProviderNames.Razorpay)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is a Razorpay `payment_id` (`pay_...`) from
client-side Razorpay Checkout for server-side capture.

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
