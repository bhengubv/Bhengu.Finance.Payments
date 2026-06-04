# Bhengu.Finance.Payments.EcoCash

EcoCash adapter for the Bhengu.Finance.Payments family — Zimbabwe's dominant mobile-money operator. C2B instant charges, refunds, and B2C payouts via the EcoCash Developers v2 REST API, behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.EcoCash
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `EcoCashPaymentProvider` | Charge / refund / webhook verify (C2B) |
| `IPayoutProvider` | `EcoCashPaymentProvider` | B2C disbursement |

## Wiring

```csharp
builder.Services.AddEcoCashPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:EcoCash`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "EcoCash": {
          "ApiKey": "...",
          "Username": "...",
          "Password": "...",
          "MerchantCode": "...",
          "MerchantPin": "...",
          "MerchantNumber": "263771234567",
          "NotifyUrl": "https://example.com/ecocash/callback", // optional
          "UseSandbox": true,
          "BaseUrl": null                                       // optional override
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
    [FromKeyedServices(ProviderNames.EcoCash)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is the payer's MSISDN in international format
(e.g. `263771234567`).

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Refund))
    await gateway.ProcessRefundAsync(refundRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
