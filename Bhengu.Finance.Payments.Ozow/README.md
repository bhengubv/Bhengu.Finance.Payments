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
          "PrivateKey": "...",       // used for the SHA-512 HashCheck + webhook hash
          "ApiKey": "...",           // sent on the ApiKey header for api.ozow.com status calls
          "CountryCode": "ZA",       // posted as CountryCode (part of the HashCheck)
          "UseSandbox": false,       // maps to Ozow's IsTest flag
          "PaymentBaseUrl": null,    // optional override of the pay.ozow.com redirect host
          "ApiBaseUrl": null         // optional override of the api.ozow.com status host
        }
      }
    }
  }
}
```

## How the charge works (redirect flow)

Ozow's customer payment is a **redirect**, not a JSON API call. `ProcessPaymentAsync` builds a signed request
(the post variables plus a SHA-512 `HashCheck`) and returns a `PaymentResponse` with `Status = Pending` and the
`https://pay.ozow.com/...` URL in `RedirectUrl`. Send the payer there to complete the payment; the outcome arrives
on your `NotifyUrl` webhook (parse it with `ParseWebhookAsync`) and can be reconciled server-side via
`GetTransactionByReferenceAsync` / `GetTransactionAsync` (both hit `api.ozow.com` with the `ApiKey` header).

Pass the redirect URLs and reference via `PaymentRequest.Metadata`:
`transaction_reference`, `success_url`, `cancel_url`, `error_url`, `notify_url`.

Sources: <https://ozow.com/integrations>, <https://hub.ozow.com/docs>.

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
