# Bhengu.Finance.Payments.Hubtel

Hubtel adapter for the Bhengu.Finance.Payments family — hosted checkout, refunds, mobile-money send-money (payouts), and settlement reconciliation for Ghana via the Hubtel REST API, behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Hubtel
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `HubtelPaymentProvider` | Charge (redirect) / refund / webhook verify |
| `IPayoutProvider` | `HubtelPaymentProvider` | Mobile-money send-money disbursement |
| `ISettlementProvider` | `HubtelSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddHubtelPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Hubtel`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Hubtel": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantAccountNumber": "POS-...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://example.com/webhooks/hubtel",
          "ReturnUrl": "https://example.com/hubtel/return",
          "Currency": "GHS",
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
    [FromKeyedServices(ProviderNames.Hubtel)] IPaymentGatewayProvider gateway) : ControllerBase
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
