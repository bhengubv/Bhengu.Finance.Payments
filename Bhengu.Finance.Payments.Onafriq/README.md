# Bhengu.Finance.Payments.Onafriq

Onafriq (formerly MFS Africa) adapter for the Bhengu.Finance.Payments family. Cross-border wallet-to-wallet transfers across 35+ African countries — primarily a disbursement rail, with collections as a secondary capability. Charge, webhook verification, and payouts behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Onafriq
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `OnafriqPaymentProvider` | Charge (collection) / webhook verify; no refund API upstream |
| `IPayoutProvider` | `OnafriqPaymentProvider` | Wallet-to-wallet cross-border disbursement |

## Wiring

```csharp
builder.Services.AddOnafriqPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Onafriq`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Onafriq": {
          "ApiKey": "...",
          "MerchantId": "...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://example.com/webhooks/onafriq",
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
public class PayoutsController(
    [FromKeyedServices(ProviderNames.Onafriq)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("payout")]
    public async Task<PayoutResponse> Payout([FromBody] PayoutRequest request)
        => await gateway.ProcessPayoutAsync(request);
}
```

`PayoutRequest.DestinationToken` format: `"<country>:<walletNumber>"`
(e.g. `"GH:233244000000"`).

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
