# Bhengu.Finance.Payments.Mukuru

Mukuru adapter for the Bhengu.Finance.Payments family — B2B outbound remittance from South Africa to Zimbabwe, Malawi, Mozambique, Zambia, Ghana, Kenya, Uganda, Nigeria, Tanzania, and Côte d'Ivoire via cash pickup, mobile money, or bank transfer. Charge, refund (pre-collection only), webhook verification, payouts, and recurring mandates behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Mukuru
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `MukuruPaymentProvider` | Charge (wallet top-up) / refund / webhook verify |
| `IPayoutProvider` | `MukuruPaymentProvider` | Cross-border remittance disbursement |
| `IMandateProvider` | `MukuruMandateProvider` | Debit-order / pull-payment mandates |

## Wiring

```csharp
builder.Services.AddMukuruPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Mukuru`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Mukuru": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantId": "...",
          "WebhookSecret": "...",
          "SenderCountry": "ZA",
          "DefaultCurrency": "ZAR",
          "CallbackUrl": "https://example.com/webhooks/mukuru",
          "UseSandbox": true,
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
    [FromKeyedServices(ProviderNames.Mukuru)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("payout")]
    public async Task<PayoutResponse> Payout([FromBody] PayoutRequest request)
        => await gateway.ProcessPayoutAsync(request);
}
```

`PayoutRequest.DestinationToken` format: `"<country>:<method>:<accountOrMsisdn>[:bankCode]"`
(e.g. `"MW:MOBILE_MONEY:265888123456"`).

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Payout))
    await gateway.ProcessPayoutAsync(payoutRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
