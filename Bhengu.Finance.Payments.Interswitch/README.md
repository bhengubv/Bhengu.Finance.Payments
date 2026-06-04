# Bhengu.Finance.Payments.Interswitch

Interswitch adapter for the Bhengu.Finance.Payments family — pan-African (Nigeria-led) Quickteller Pay and Disbursement REST APIs covering Verve, Mastercard, and Visa rails. Charge, refund, webhook verification, payouts, tokenisation (vaulted and SAQ-D raw card), and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Interswitch
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `InterswitchPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `InterswitchPaymentProvider` | Bank disbursement |
| `ITokenisationProvider` | `InterswitchTokenisationProvider` | Read vaulted payment methods |
| `IRawCardTokenisationProvider` | `InterswitchRawCardTokenisationProvider` | SAQ-D — accepts raw PAN+CVV |
| `ISettlementProvider` | `InterswitchSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddInterswitchPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Interswitch`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Interswitch": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantCode": "MX...",
          "ProductId": "...",
          "TerminalId": null,         // optional
          "WebhookSecret": "...",
          "UseSandbox": true,
          "BaseUrl": null,            // optional override
          "SandboxUrl": null          // optional override
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
    [FromKeyedServices(ProviderNames.Interswitch)] IPaymentGatewayProvider gateway) : ControllerBase
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
