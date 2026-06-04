# Bhengu.Finance.Payments.Cellulant

Cellulant (Tingg / Mula) adapter for the Bhengu.Finance.Payments family. Pan-African aggregator covering 35+ countries — single integration for cards, mobile money, and bank transfers, plus marketplace splits and settlement reconciliation, behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Cellulant
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `CellulantPaymentProvider` | Charge / refund / webhook verify via Tingg Checkout |
| `IPayoutProvider` | `CellulantPaymentProvider` | Disbursements via Mula |
| `IMarketplaceProvider` | `CellulantMarketplaceProvider` | Split payments + sub-accounts |
| `ISettlementProvider` | `CellulantSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddCellulantPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Cellulant`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Cellulant": {
          "ServiceCode": "BIL-...",
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantTransactionId": "tgn",                     // optional prefix
          "CallbackUrl": "https://example.com/webhooks/tingg",
          "WebhookSecret": "...",
          "CountryCode": "KE",
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
    [FromKeyedServices(ProviderNames.Cellulant)] IPaymentGatewayProvider gateway) : ControllerBase
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
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
