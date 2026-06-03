# Bhengu.Finance.Payments.ChipperCash

Chipper Cash provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Pan-African mobile-money collections and disbursements: Nigeria · Ghana · Kenya · Uganda · Tanzania · Rwanda · South Africa · USA.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.ChipperCash
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "ChipperCash": {
          "ApiKey": "...",
          "ApiSecret": "...",
          "MerchantId": "...",
          "CallbackUrl": "https://yoursite.example/webhooks/chipper",
          "Country": "NG",
          "Currency": "NGN",
          "UseSandbox": false
        }
      }
    }
  }
}
```

Required: `ApiKey` (sent as the literal `Authorization` header) and `ApiSecret` (HMAC-SHA256 signs every request body — `<json>.<timestamp>` — and verifies webhooks).

## Wire it up

```csharp
builder.Services.AddChipperCashPayments(builder.Configuration);
```

Validates `ApiKey` and `ApiSecret` at registration.

## `PaymentMethodToken` semantics

**Fallback for `msisdn`** — used as the payer's mobile number when no explicit `msisdn` metadata is supplied. Format: international (e.g. `233244000000`).

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `reference` | Optional | Merchant ref (defaults to `chp-<guid>`) | `order-123` |
| `msisdn` | Optional | Phone (defaults to `PaymentMethodToken`) | `233244000000` |
| `network` | Optional | Mobile network code (defaults to `MTN`) | `MTN`, `VODAFONE`, `AIRTEL` |
| `country` | Optional | ISO-3166 alpha-2 (defaults to `Country` option) | `GH` |

## `PayoutRequest.DestinationToken` format

Just the recipient's MSISDN (e.g. `233244000000`) — sent as `destination.mobile.msisdn`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` calls `POST v1/collections` and returns `Pending`. The payer receives an STK push on their phone; final outcome arrives via webhook to `CallbackUrl`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST v1/collections/{id}/refund` with `amount` and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST v1/disbursements`.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `X-Chipper-Signature` header (`ApiSecret`-keyed).

```csharp
app.MapPost("/webhooks/chipper", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.ChipperCash)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Chipper-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `collection.successful`, `disbursement.successful`, `payment.successful`, `collection.failed`, `disbursement.failed`, `payment.failed`, `refund.successful`, `refund.processed`.

## Capabilities

`Charge | Refund | Payout | Webhook | MobileMoney | CrossBorder`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
