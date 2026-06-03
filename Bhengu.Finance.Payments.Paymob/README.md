# Bhengu.Finance.Payments.Paymob

Paymob (Egypt / GCC / Pakistan) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Hosted iframe checkout and disbursements via Paymob Accept's 4-step auth-handshake REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Paymob
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Paymob": {
          "ApiKey": "...",
          "HmacSecret": "...",
          "IntegrationId": 123456,
          "IframeId": 78910,
          "Currency": "EGP",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ApiKey` (used to mint auth tokens via `api/auth/tokens`). `HmacSecret` is HMAC-SHA512 secret for webhook verification. `IntegrationId` and `IframeId` are the Paymob dashboard ids that wire the merchant to a specific card processor and iframe; they may also come from metadata per-request.

## Wire it up

```csharp
builder.Services.AddPaymobPayments(builder.Configuration);
```

Validates `ApiKey` at registration.

## `PaymentMethodToken` semantics

**Unused.** The Paymob 4-step Accept flow (authenticate → create order → create payment key → return iframe URL) doesn't accept a pre-tokenised payment method — card details are collected on the Paymob iframe.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `integration_id` | Required (or set in options) | Integer | `123456` |
| `iframe_id` | Optional (or set in options) | Integer | `78910` |
| `merchant_order_id` | Optional | Merchant ref | `order-123` |
| `email` | Optional | E-mail (defaults to `na@na.na`) | `buyer@example.com` |
| `first_name` | Optional | Given name (defaults to `NA`) | `Thandi` |
| `last_name` | Optional | Family name (defaults to `NA`) | `Bhengu` |
| `phone_number` | Optional | Phone (defaults to `+20000000000`) | `+201001234567` |

Missing `integration_id` (with no option default) throws `PaymentDeclinedException` with `ProviderErrorCode = "missing_integration_id"`.

## `PayoutRequest.DestinationToken` format

A Paymob **destination identifier** (wallet number or bank reference, depending on disbursement type) passed verbatim as `destination`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending` plus the iframe URL as `RedirectUrl` (or the raw payment token if no `iframe_id` is set). The payer completes on the iframe; the real outcome arrives via webhook. Amounts sent in piastres / smallest unit (Amount × 100).

## Refunds

Yes — `ProcessRefundAsync` calls `POST api/acceptance/void_refund/refund` with `transaction_id` and `amount_cents`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST api/disbursements/transactions` against the Paymob Disbursement API.

## Webhook

HMAC-SHA512 of the canonical body, hex-encoded lowercase, in the `hmac` query string parameter (per Paymob spec — the caller is responsible for reconstructing the canonical concatenation of fields before calling `VerifyWebhookSignature`).

```csharp
app.MapPost("/webhooks/paymob", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Paymob)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var hmac = ctx.Request.Query["hmac"].ToString();

    var canonical = BuildPaymobCanonical(body);
    if (!provider.VerifyWebhookSignature(canonical, hmac))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Webhook event fields mapped: `is_refunded` → Refunded; `is_voided` → Cancelled; `pending` → Pending; `success=true` → Completed; `success=false` → Failed.

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
