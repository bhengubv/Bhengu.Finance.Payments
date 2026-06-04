# Bhengu.Finance.Payments.AirtelMoney

Airtel Money mobile-money adapter for the Bhengu.Finance.Payments family. Covers Airtel's pan-African footprint (Kenya, Uganda, Tanzania, Zambia, Malawi, DRC, Niger, Gabon, Madagascar and more) with charge, refund, webhook verification, and B2C disbursement payouts behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.AirtelMoney
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `AirtelMoneyPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `AirtelMoneyPaymentProvider` | Wallet-to-wallet disbursement |
| `IPayoutProvider` | `AirtelMoneyPayoutProvider` | Standalone payout adapter (B2C transfers) |

## Wiring

```csharp
builder.Services.AddAirtelMoneyPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:AirtelMoney`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "AirtelMoney": {
          "ClientId": "...",
          "ClientSecret": "...",
          "Country": "KE",
          "Currency": "KES",
          "CallbackUrl": "https://example.com/webhooks/airtelmoney",
          "WebhookSecret": "...",
          "EncryptedDisbursementPin": "...",   // optional, required for payouts
          "UseSandbox": true,
          "BaseUrl": null                       // optional override
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
    [FromKeyedServices(ProviderNames.AirtelMoney)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is the payer's MSISDN in international format
(e.g. `254712345678` for Kenya).

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
