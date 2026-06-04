# Bhengu.Finance.Payments.ApplePay

Apple Pay adapter for the Bhengu.Finance.Payments family. Apple Pay only **tokenises** — it does not settle. This package validates the PKPaymentToken, tags the request with `payment_source=apple_pay`, and forwards the charge to a real downstream processor (Stripe, etc.) registered alongside it.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Stripe   # or another downstream
dotnet add package Bhengu.Finance.Payments.ApplePay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `ApplePayPaymentProvider` | Validates PKPaymentToken, forwards to the configured `DownstreamProcessor` |

The provider also implements `IRequiresPostConstructionValidation` so an invalid
`DownstreamProcessor` value crashes the app at startup with a clear
`ProviderConfigurationException`, not on the first request.

## Wiring

```csharp
builder.Services.AddStripePayments(builder.Configuration);     // downstream first
builder.Services.AddApplePayPayments(builder.Configuration);   // validates downstream at startup
```

Bind options from `Bhengu:Finance:Payments:ApplePay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "ApplePay": {
          "MerchantId": "merchant.com.your-app",
          "DownstreamProcessor": "stripe",
          "DomainName": "yoursite.co.za",                  // optional
          "MerchantCertificatePath": null,                  // optional
          "MerchantCertificatePassword": null               // optional
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
    [FromKeyedServices(ProviderNames.ApplePay)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is the serialised PKPaymentToken JSON from your client.

## Capabilities at runtime

```csharp
// Refund support inherits from the downstream processor
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Refund))
    await gateway.ProcessRefundAsync(refundRequest);
```

Apple Pay has no webhook channel of its own — `VerifyWebhookSignature` returns
`false` and `ParseWebhookAsync` returns `null`. Wire your webhook endpoint to
the downstream processor instead.

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
