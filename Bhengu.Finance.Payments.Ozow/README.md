# Bhengu.Finance.Payments.Ozow

Ozow adapter for the Bhengu.Finance.Payments family — instant EFT and PayShap pay-by-bank for South Africa via Ozow's hosted redirect flow. Charge, refund, and webhook verification behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Ozow
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `OzowPaymentProvider` | Charge (redirect) / refund / webhook verify |

## Wiring

```csharp
builder.Services.AddOzowPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Ozow`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Ozow": {
          "SiteCode": "ABC123",
          "PrivateKey": "...",
          "ApiKey": "...",
          "UseSandbox": false,
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
    [FromKeyedServices(ProviderNames.Ozow)] IPaymentGatewayProvider gateway) : ControllerBase
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
