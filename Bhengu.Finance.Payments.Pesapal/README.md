# Bhengu.Finance.Payments.Pesapal

Pesapal adapter for the Bhengu.Finance.Payments family — hosted-payment-page checkout (cards, M-Pesa, Airtel Money, EFT) across Kenya, Uganda, Tanzania, Rwanda, Zambia and Malawi via Pesapal API 3.0. Charge, refund, IPN webhook verification, recurring subscriptions, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Pesapal
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PesapalPaymentProvider` | Charge (redirect) / refund / IPN verify |
| `ISubscriptionProvider` | `PesapalSubscriptionProvider` | Plans + subscriptions |
| `ISettlementProvider` | `PesapalSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddPesapalPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Pesapal`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Pesapal": {
          "ConsumerKey": "...",
          "ConsumerSecret": "...",
          "IpnId": "...",
          "CallbackUrl": "https://example.com/pesapal/return",
          "IpnUrl": "https://example.com/webhooks/pesapal",
          "Currency": "KES",
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
    [FromKeyedServices(ProviderNames.Pesapal)] IPaymentGatewayProvider gateway) : ControllerBase
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
