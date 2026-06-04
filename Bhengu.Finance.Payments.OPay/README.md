# Bhengu.Finance.Payments.OPay

OPay adapter for the Bhengu.Finance.Payments family — OPay International cashier across Nigeria, Egypt, and Pakistan. Hosted checkout, refunds, payouts, tokenisation (vaulted + SAQ-D raw card), and settlement reconciliation via the OPay REST API behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.OPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `OPayPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `OPayPaymentProvider` | Cross-border payouts |
| `ITokenisationProvider` | `OPayTokenisationProvider` | Read vaulted card tokens |
| `IRawCardTokenisationProvider` | `OPayRawCardTokenisationProvider` | SAQ-D — accepts raw PAN+CVV |
| `ISettlementProvider` | `OPaySettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddOPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:OPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "OPay": {
          "PublicKey": "OPAYPUB...",
          "SecretKey": "OPAYPRV...",
          "MerchantId": "...",
          "Country": "NG",
          "CallbackUrl": "https://example.com/webhooks/opay",
          "ReturnUrl": "https://example.com/opay/return",
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
    [FromKeyedServices(ProviderNames.OPay)] IPaymentGatewayProvider gateway) : ControllerBase
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
