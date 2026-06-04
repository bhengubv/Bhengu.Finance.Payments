# Bhengu.Finance.Payments.ChipperCash

Chipper Cash adapter for the Bhengu.Finance.Payments family. Pan-African mobile-money collections and disbursements covering Nigeria, Ghana, Kenya, Uganda, Tanzania, Rwanda, South Africa and the USA — charge, refund, webhook verification, payouts, and tokenised payment methods behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.ChipperCash
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `ChipperCashPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `ChipperCashPaymentProvider` | Disbursements via `v1/disbursements` |
| `ITokenisationProvider` | `ChipperCashTokenisationProvider` | Read vaulted payment methods |

## Wiring

```csharp
builder.Services.AddChipperCashPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:ChipperCash`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "ChipperCash": {
          "ApiKey": "...",
          "ApiSecret": "...",
          "MerchantId": "...",
          "CallbackUrl": "https://example.com/webhooks/chipper",
          "Country": "NG",
          "Currency": "NGN",
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
    [FromKeyedServices(ProviderNames.ChipperCash)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is the payer's MSISDN in international format
(e.g. `233244000000`).

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
