# Bhengu.Finance.Payments.JamboPay

JamboPay (Kenya) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Cards, M-Pesa, Airtel Money, and bank rails for Kenya via the JamboPay v1 REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.JamboPay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "JamboPay": {
          "ApiKey": "...",
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantCode": "...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://yoursite.example/webhooks/jambopay",
          "Currency": "KES"
        }
      }
    }
  }
}
```

Required: `ApiKey` (sent on `x-api-key`), `ClientId` + `ClientSecret` (OAuth2 client_credentials against `/oauth/token`), and `MerchantCode`.

## Wire it up

```csharp
builder.Services.AddJamboPayPayments(builder.Configuration);
```

Validates all four required options at registration. Tokens cached until 30s before expiry.

## `PaymentMethodToken` semantics

The merchant **`transaction_ref`** — your reference. JamboPay returns the same value (or its own gateway ref) in `GatewayReference`.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `email` | Optional | E-mail | `buyer@example.com` |
| `msisdn` | Optional | Phone number | `254712345678` |
| `name` | Optional | Display name | `Thandi Bhengu` |
| `payment_method` | Optional | `CARD`, `MPESA`, `AIRTEL`, `BANK` (defaults to `CARD`) | `MPESA` |

## `PayoutRequest.DestinationToken` format

Two forms:
- `"msisdn:<phone>"` — mobile-money payout (e.g. `"msisdn:254700000000"`)
- `"bank:<bankCode>:<accountNumber>"` — bank payout (e.g. `"bank:KCBLKENX:1234567890"`)

Invalid format throws `BhenguPaymentException`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` calls `payments/initiate` and returns `Pending` plus the hosted-checkout URL as `RedirectUrl`. Final outcome arrives via webhook.

## Refunds

Yes — `ProcessRefundAsync` calls `POST payments/refund` with `transaction_ref`, `amount`, and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST payouts/initiate`.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `x-jambopay-signature` header.

```csharp
app.MapPost("/webhooks/jambopay", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.JamboPay)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["x-jambopay-signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `payment.completed`, `payment.success`, `payment.failed`, `payment.cancelled`, `refund.completed`, `payout.completed`, `payout.failed`.

## Capabilities

`Charge | Refund | Payout | Webhook | Cards | MobileMoney | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
