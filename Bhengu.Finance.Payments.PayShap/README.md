# Bhengu.Finance.Payments.PayShap

PayShap adapter for the Bhengu.Finance.Payments family — South Africa's BankservAfrica RTC real-time interbank rail. Instant ZAR account-to-account transfers using explicit account numbers or proxy aliases (MSISDN / e-mail / ID / business), with QR generation for merchant-presented pay flows, behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayShap
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `PayShapPaymentProvider` | Real-time A2A credit transfer / webhook verify |
| `IQrCodeProvider` | `PayShapQrCodeProvider` | Merchant-presented PayShap QR generation |

PayShap also ships a richer `IPayShapService` for proxy resolution, account
verification, and multi-step settlement — inject it directly when the generic
`IPaymentGatewayProvider` surface isn't enough.

## Wiring

```csharp
builder.Services.AddPayShapServices(builder.Configuration);
```

Bind options from the `PayShapSettings` root section (the rich service and the
gateway adapter share one config block):

```jsonc
{
  "PayShapSettings": {
    "ApiBaseUrl": "https://api.payshap.co.za",
    "ApiKey": "...",
    "ApiSecret": "...",
    "SignatureKey": "...",
    "MerchantId": "...",
    "Payee": {                       // optional, used by the QR provider
      "BankCode": "250655",
      "Account": "1234567890",
      "Name": "Vendor Co.",
      "IdentifierType": "MSISDN",
      "IdentifierValue": "+27821234567"
    }
  }
}
```

## Usage

```csharp
[ApiController]
public class CheckoutController(
    [FromKeyedServices(ProviderNames.PayShap)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Webhook))
    var evt = await gateway.ParseWebhookAsync(body);

if (gateway is IQrCodeProvider qr)
    var image = await qr.GenerateAsync(qrRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
