# Bhengu.Finance.Payments.MTNMoMo

MTN Mobile Money (MoMo) adapter for the Bhengu.Finance.Payments family. Collection (RequestToPay) and Disbursement (Transfer) via the MoMo Open API across Uganda, Ghana, Côte d'Ivoire, Cameroon, Zambia, Rwanda, Benin, Congo, Guinea and Liberia, behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.MTNMoMo
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `MTNMoMoPaymentProvider` | RequestToPay charge / callback verify (no refund API upstream) |
| `IPayoutProvider` | `MTNMoMoPaymentProvider` | Disbursement Transfer |
| `IPayoutProvider` | `MTNMoMoPayoutProvider` | Standalone payout adapter |

## Wiring

```csharp
builder.Services.AddMTNMoMoPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:MTNMoMo`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "MTNMoMo": {
          "SubscriptionKey": "...",
          "ApiUserId": "...",
          "ApiKey": "...",
          "TargetEnvironment": "sandbox",   // or "mtnuganda", "mtnghana", ...
          "CallbackUrl": "https://example.com/momo/callback",
          "UseSandbox": true,
          "BaseUrl": null                    // optional override
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
    [FromKeyedServices(ProviderNames.MTNMoMo)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is the payer's MSISDN in international format
(e.g. `256777123456` for Uganda).

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
