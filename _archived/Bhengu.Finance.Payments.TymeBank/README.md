# Bhengu.Finance.Payments.TymeBank

TymeBank adapter for the Bhengu.Finance.Payments family — South African pay-by-bank instant transfers, Scan-to-Pay QR codes, and EFT/PayShap payouts via TymeBank's developer API. Charge, refund, webhook verification, payouts, and recurring debit-order mandates behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.TymeBank
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `TymeBankPaymentProvider` | Instant pay-by-bank charge / refund / webhook verify |
| `IPayoutProvider` | `TymeBankPaymentProvider` | EFT / PayShap disbursement |
| `IMandateProvider` | `TymeBankMandateProvider` | Debit-order / pull-payment mandates |

## Wiring

```csharp
builder.Services.AddTymeBankPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:TymeBank`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "TymeBank": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantId": "...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://example.co.za/webhooks/tyme",
          "Currency": "ZAR",
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
    [FromKeyedServices(ProviderNames.TymeBank)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PayoutRequest.DestinationToken` format: `"<bankCode>:<accountNumber>[:<beneficiaryName>]"`
(e.g. `"250655:1234567890:Jane Doe"`).

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
