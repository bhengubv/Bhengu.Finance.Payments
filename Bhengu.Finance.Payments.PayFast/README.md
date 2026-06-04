# Bhengu.Finance.Payments.PayFast

PayFast adapter for the Bhengu.Finance.Payments family — South Africa's leading hosted-checkout and ad-hoc-subscription gateway. Charge, webhook (ITN) verification, recurring subscriptions with pause/resume, and debit-order mandates behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayFast
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PayFastPaymentProvider` | Charge (redirect) / ITN webhook verify; refunds via dashboard |
| `ISubscriptionProvider` | `PayFastSubscriptionProvider` | Ad-hoc tokenisation-based subscriptions |
| `ISubscriptionPauseSupport` | `PayFastSubscriptionProvider` | Pause / resume billing |
| `IMandateProvider` | `PayFastMandateProvider` | Debit-order / pull-payment mandates |

## Wiring

```csharp
builder.Services.AddPayFastPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:PayFast`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PayFast": {
          "MerchantId": "10000100",
          "MerchantKey": "46f0cd694581a",
          "Passphrase": "...",
          "UseSandbox": true,
          "ReturnUrl": "https://example.co.za/payfast/return",   // optional
          "CancelUrl": "https://example.co.za/payfast/cancel",   // optional
          "NotifyUrl": "https://example.co.za/payfast/itn",      // optional
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
    [FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

For first-time customers (no token yet), use `PayFastFormBuilder` (also registered)
to build a hosted-redirect URL.

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Webhook))
    var evt = await gateway.ParseWebhookAsync(body);

if (gateway is ISubscriptionPauseSupport pause)
    await pause.PauseSubscriptionAsync(subscriptionId);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
