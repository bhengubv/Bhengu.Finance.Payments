# Bhengu.Finance.Payments.Kashier

Kashier adapter for the Bhengu.Finance.Payments family — server-to-server card charges (`POST /checkout`), refunds, hosted-payment-page redirect, webhook verification, vaulted tokenisation and 3-D Secure for Egypt via the Kashier REST API. Charge, refund, order reconciliation, webhook verification, vaulted tokenisation and 3-D Secure behind the Bhengu canonical contracts.

> **Verification status: `DocsOnly`.** The wire format is built from Kashier's public documentation and reference SDKs (kashier.io/docs/integration-guide, developers.kashier.io, the Kashier-payments GitHub demos, and the asciisd/kashier SDK) and is unit-tested, but has never been sandbox-verified. Kashier does **not** publicly document a server-side payout/disbursement API, so this package does **not** implement `IPayoutProvider`. A few endpoints whose exact request/response shapes Kashier does not publish (the 3-D Secure ACS fields, the tokenisation request cipher, the token-delete path) are marked `// UNVERIFIED:` in source.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Kashier
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `KashierPaymentProvider` | Charge (`POST /checkout`) / refund (`PUT /orders/{orderId}/transactions/{transactionId}?operation=refund`) / order reconciliation / webhook verify |
| `ITokenisationProvider` | `KashierTokenisationProvider` | List / fetch / delete vaulted card tokens (`GET /tokens`) |
| `IRawCardTokenisationProvider` | `KashierRawCardTokenisationProvider` | Vault a raw card (`POST /tokenization`) — SAQ-D scope |
| `IThreeDSecureProvider` | `KashierThreeDSecureProvider` | SCA step-up via the checkout flow |

`IPayoutProvider` is intentionally **not** implemented — Kashier publishes no server payout API, and a guessed payout path would be worse than honestly not supporting it.

## Wiring

```csharp
builder.Services.AddKashierPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Kashier`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Kashier": {
          "ApiKey": "...",          // Payment API Key — verifies webhook signatures (x-kashier-signature)
          "MerchantId": "MID-...",
          "SecretKey": "...",        // Secret Key — Authorization header on REST calls + order-hash key
          "WebhookSecret": "",       // optional; leave blank — webhooks are signed with the Payment API Key
          "Currency": "EGP",
          "Mode": "test",
          "RedirectUrl": "https://example.com/kashier/return",          // optional
          "ServerWebhookUrl": "https://example.com/webhooks/kashier",   // optional
          "UseSandbox": true,                                            // true → test-api.kashier.io, false → api.kashier.io
          "BaseUrl": null,                                               // optional REST base override
          "HostedPaymentBaseUrl": null                                   // optional HPP base override (default https://checkout.kashier.io)
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
    [FromKeyedServices(ProviderNames.Kashier)] IPaymentGatewayProvider gateway) : ControllerBase
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

if (gateway is IThreeDSecureProvider tds)
    var challenge = await tds.StartAuthenticationAsync(intent);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
