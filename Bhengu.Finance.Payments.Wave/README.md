# Bhengu.Finance.Payments.Wave

Wave adapter for the Bhengu.Finance.Payments family — hosted Checkout Sessions for collections and Payouts for disbursements across Senegal, Côte d'Ivoire, Mali, and Uganda via the Wave Business REST API. Charge, refund, webhook verification, and payouts behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Wave
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `WavePaymentProvider` | Checkout Session charge / refund / webhook verify |
| `IPayoutProvider` | `WavePaymentProvider` | Wallet disbursement |
| `IPayoutProvider` | `WavePayoutProvider` | Standalone payout adapter |

## Wiring

```csharp
builder.Services.AddWavePayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Wave`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Wave": {
          "ApiKey": "wave_sn_test_...",
          "WebhookSecret": "...",
          "Currency": "XOF",
          "SuccessUrl": "https://example.com/wave/success",   // optional
          "ErrorUrl": "https://example.com/wave/error",       // optional
          "BaseUrl": null                                      // optional override
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
    [FromKeyedServices(ProviderNames.Wave)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PayoutRequest.DestinationToken` format: `"<countryCode>:<phone>"` or just `<phone>`
(defaults to `SN`). Example: `"SN:221761234567"`.

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
