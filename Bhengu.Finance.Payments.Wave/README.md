# Bhengu.Finance.Payments.Wave

Wave provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Countries: Senegal · Côte d'Ivoire · Mali · Uganda. Wave's hosted Checkout Sessions for collections and Payouts for disbursements via the Wave Business REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Wave
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Wave": {
          "ApiKey": "wave_sn_test_...",
          "WebhookSecret": "...",
          "Currency": "XOF",
          "SuccessUrl": "https://yoursite.example/wave/success",
          "ErrorUrl": "https://yoursite.example/wave/error"
        }
      }
    }
  }
}
```

Required: `ApiKey` (Bearer token on every request). `WebhookSecret` is HMAC-SHA256 secret for signature verification.

## Wire it up

```csharp
builder.Services.AddWavePayments(builder.Configuration);
```

Validates `ApiKey` at registration.

## `PaymentMethodToken` semantics

**Client reference** — Wave stamps it on the Checkout Session as `client_reference`. Use your order id or any merchant string; Wave doesn't tokenise the payer's wallet.

## Metadata

`Metadata` is not read by the provider. Pass merchant metadata via `PaymentMethodToken` (as `client_reference`) instead.

## `PayoutRequest.DestinationToken` format

Either `"<countryCode>:<phone>"` or just `<phone>` (defaults to `SN` country).

Examples:
- `"SN:221761234567"` — Senegal
- `"CI:2250712345678"` — Côte d'Ivoire
- `"221761234567"` — defaults to Senegal

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending` plus a `RedirectUrl` (`wave_launch_url` — open in the Wave app or browser). Final outcome arrives via webhook.

Amounts are sent as integer whole-units (XOF/XAF are not subunit currencies).

## Refunds

Yes — `ProcessRefundAsync` calls `POST v1/checkout/sessions/{sessionId}/refund` with an empty body. Wave refunds the full session amount.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST v1/payouts` with the recipient's MSISDN and country.

## Webhook

HMAC-SHA256 of `<timestamp>.<payload>`, hex-encoded lowercase. The header format is `Wave-Signature: t=<unix>,v1=<sig>`.

```csharp
app.MapPost("/webhooks/wave", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Wave)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["Wave-Signature"].ToString();  // "t=...,v1=..."

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `checkout.session.completed`, `checkout.session.payment_succeeded`, `checkout.session.payment_failed`, `merchant.payment_refunded`, `checkout.session.refunded`, `payout.completed`, `payout.failed`.

## Capabilities

`Charge | Refund | Payout | Webhook | MobileMoney | RedirectFlow`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
