# Bhengu.Finance.Payments.Flutterwave

Flutterwave adapter for the Bhengu.Finance.Payments family. Pan-African aggregator covering 34+ countries — cards, bank transfers, mobile money (M-Pesa, MoMo, Airtel), USSD, plus marketplace splits, disputes, subscriptions, settlement reconciliation, and tokenisation behind the Bhengu canonical contracts. Wraps the Flutterwave v3 REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Flutterwave
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `FlutterwavePaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `FlutterwavePaymentProvider` | Transfers via `v3/transfers` |
| `ITokenisationProvider` | `FlutterwaveTokenisationProvider` | Read vaulted payment methods |
| `ISubscriptionProvider` | `FlutterwaveSubscriptionProvider` | Plans + subscriptions |
| `IDisputeProvider` | `FlutterwaveDisputeProvider` | Chargeback lifecycle |
| `IMarketplaceProvider` | `FlutterwaveMarketplaceProvider` | Split payments + sub-accounts |
| `ISettlementProvider` | `FlutterwaveSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddFlutterwavePayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Flutterwave`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Flutterwave": {
          "SecretKey": "FLWSECK_TEST-...",
          "PublicKey": "FLWPUBK_TEST-...",
          "EncryptionKey": "...",
          "WebhookSecret": "...",
          "RedirectUrl": "https://example.com/flw/return",   // optional
          "BaseUrl": null                                     // optional override
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
    [FromKeyedServices(ProviderNames.Flutterwave)] IPaymentGatewayProvider gateway) : ControllerBase
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

if (gateway is IMarketplaceProvider marketplace)
    var split = await marketplace.CreateSplitAsync(splitRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
