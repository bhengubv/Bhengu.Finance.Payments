# Bhengu.Finance.Payments.BricsPay

BRICS Pay adapter for the Bhengu.Finance.Payments family — **e-commerce QR acquiring** ("Internet
Acquiring") on the BRICS Pay / Joys processing platform. The customer pays by scanning a QR on a hosted
payment page; there is no card token. You create a transaction, show the returned QR/payment-page URL, and
confirm the result by callback or status poll. Full and partial refunds are supported.

> **Built from BRICS Pay's published E-Commerce protocol — not yet verified against a live terminal.**
> BRICS Pay has no self-serve sandbox: a terminal is provisioned at onboarding (contract → terminal ID →
> register your public key), and the API base URL is issued to you then. This provider is marked
> `DocsOnly` until verified against a real terminal. See
> [`BRICS_PAY_API_REFERENCE.md`](./BRICS_PAY_API_REFERENCE.md).

## Install

```sh
dotnet add package Bhengu.Finance.Payments.BricsPay
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IQrCodeProvider` | `BricsPayPaymentProvider` | Create QR payment (`GenerateQrAsync`) + poll status (`GetQrStatusAsync`) |

Plus provider-specific methods on the concrete `BricsPayPaymentProvider`:

- `GetTransactionAsync(sequence)` — full transaction status.
- `RefundAsync(originalTransaction, refundSequence, amount)` — full or partial refund.
- `ParseCallback(payload)` — parse a callback body (then confirm via `GetTransactionAsync`).

BRICS Pay e-commerce is QR acquiring **only** — there is no card-charge gateway, payout, or settlement-feed
API — so this package deliberately does **not** implement `IPaymentGatewayProvider`, `IPayoutProvider`, or
`ISettlementProvider`.

## Wiring

```csharp
builder.Services.AddBricsPayPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:BricsPay`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "BricsPay": {
          "TerminalId": "...",              // the "Pos" issued at onboarding
          "BaseUrl": "https://...",         // provisioned per terminal (required — no default)
          "PrivateKeyPem": "-----BEGIN PRIVATE KEY-----\n...",
          "SignatureAlgorithm": "Ecdsa",    // Ecdsa (P-256, default) or Rsa
          "CallbackUrl": "https://example.com/brics/callback",
          "ReturnUrl": "https://example.com/thank-you",
          "CssUrl": null,
          "DefaultTtlMinutes": 5
        }
      }
    }
  }
}
```

The **public** half of `PrivateKeyPem` must be registered with the processor at onboarding; the private key
stays in your config and signs every request.

## Usage

```csharp
[ApiController]
public class CheckoutController(
    [FromKeyedServices(ProviderNames.BricsPay)] IQrCodeProvider brics) : ControllerBase
{
    [HttpPost("checkout")]
    public async Task<string> Checkout([FromBody] QrCodeRequest request)
    {
        // request.Amount is required (no static QR); request.PayerIdentifier must carry
        // SHA-256(buyer IP + User-Agent) — the BRICS Pay "User" field.
        var qr = await brics.GenerateQrAsync(request);
        return qr.Payload!;   // the hosted payment-page URL containing the QR
    }
}
```

Confirm completion by polling (or rely on your registered callback):

```csharp
var status = await brics.GetQrStatusAsync(request.MerchantReference);
// PaymentStatus.Pending until paid; Completed once paid + processed; Failed if processed-but-unpaid.
```

## Status

- `DocsOnly` — implemented against the published protocol, never verified against a live terminal.
- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
