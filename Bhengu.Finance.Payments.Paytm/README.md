# Bhengu.Finance.Payments.Paytm

Paytm adapter for the Bhengu.Finance.Payments family — All-in-One Payments for India via Paytm's `theia` initiate-transaction + hosted checkout, Paytm Payouts, and refunds. Charge, refund, webhook verification, payouts, vaulted card tokenisation, recurring subscriptions, UPI QR generation, and settlement reconciliation behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Paytm
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PaytmPaymentProvider` | Charge (redirect) / refund / webhook verify |
| `IPayoutProvider` | `PaytmPaymentProvider` | Paytm Payouts wallet disbursement |
| `ITokenisationProvider` | `PaytmTokenisationProvider` | Read vaulted card tokens |
| `ISubscriptionProvider` | `PaytmSubscriptionProvider` | Plans + subscriptions |
| `IQrCodeProvider` | `PaytmQrCodeProvider` | UPI QR-code generation |
| `ISettlementProvider` | `PaytmSettlementProvider` | Reconciliation feed |

## Wiring

```csharp
builder.Services.AddPaytmPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Paytm`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Paytm": {
          "MerchantId": "MID...",
          "MerchantKey": "...",
          "WebsiteName": "DEFAULT",                              // WEBSTAGING in sandbox
          "CallbackUrl": "https://example.com/webhooks/paytm",
          "Industry": "Retail",
          "Currency": "INR",
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
    [FromKeyedServices(ProviderNames.Paytm)] IPaymentGatewayProvider gateway) : ControllerBase
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

if (gateway is IQrCodeProvider qr)
    var upi = await qr.GenerateAsync(qrRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
