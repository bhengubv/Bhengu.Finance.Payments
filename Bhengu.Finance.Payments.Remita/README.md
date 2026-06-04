# Bhengu.Finance.Payments.Remita

Remita (SystemSpecs) adapter for the Bhengu.Finance.Payments family — Nigerian government revenue collection, corporate e-collection, and Single Send Money payouts. Authentication uses SHA-512 hashes of concatenated fields with the API key (no bearer tokens). Charge, refund, webhook verification, payouts, debit-order mandates, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Remita
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `RemitaPaymentProvider` | Charge / refund / webhook verify |
| `IPayoutProvider` | `RemitaPaymentProvider` | Single Send Money disbursement |
| `IMandateProvider` | `RemitaMandateProvider` | Standing-order / direct-debit mandates |
| `ISettlementProvider` | `RemitaSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddRemitaPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Remita`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Remita": {
          "MerchantId": "...",
          "ServiceTypeId": "...",
          "ApiKey": "...",
          "ApiToken": "...",
          "FromBank": "058",                   // required for payouts
          "DebitAccount": "0123456789",        // required for payouts
          "Currency": "NGN",
          "CallbackUrl": "https://example.com/webhooks/remita",
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
    [FromKeyedServices(ProviderNames.Remita)] IPaymentGatewayProvider gateway) : ControllerBase
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
