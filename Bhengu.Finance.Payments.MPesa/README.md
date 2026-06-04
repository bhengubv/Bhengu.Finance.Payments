# Bhengu.Finance.Payments.MPesa

M-Pesa (Safaricom Daraja) adapter for the Bhengu.Finance.Payments family. STK Push C2B charges, reversal-based refunds, and B2C payouts across Kenya, Tanzania, Mozambique, DRC, Egypt, Ethiopia and Ghana — the single biggest African payment system by transaction volume — behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.MPesa
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `MPesaPaymentProvider` | STK Push charge / reversal refund / callback verify |
| `IPayoutProvider` | `MPesaPaymentProvider` | B2C disbursement |
| `IPayoutProvider` | `MPesaPayoutProvider` | Standalone payout adapter |

## Wiring

```csharp
builder.Services.AddMPesaPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:MPesa`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "MPesa": {
          "ConsumerKey": "...",
          "ConsumerSecret": "...",
          "BusinessShortCode": "174379",
          "Passkey": "...",
          "CallbackUrl": "https://example.com/mpesa/callback",
          "CallbackUrlToken": "...",
          "InitiatorName": "...",
          "SecurityCredential": "...",
          "QueueTimeoutUrl": "https://example.com/mpesa/timeout",
          "ResultUrl": "https://example.com/mpesa/result",
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
    [FromKeyedServices(ProviderNames.MPesa)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PaymentRequest.PaymentMethodToken` is the payer's MSISDN in international format
(e.g. `254712345678`). The provider issues an STK Push — the payer's phone rings
with a payment prompt; they enter their M-Pesa PIN to authorise.

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
