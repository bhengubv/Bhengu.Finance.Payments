# Bhengu.Finance.Payments.Onafriq

Onafriq (formerly MFS Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Cross-border wallet-to-wallet transfers across 35+ African countries. Primarily a **disbursement rail** — use it for payouts; collections are available but secondary.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Onafriq
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Onafriq": {
          "ApiKey": "...",
          "MerchantId": "...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://yoursite.example/webhooks/onafriq",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ApiKey` (sent on both `X-API-Key` header and as Bearer token) and `MerchantId`.

## Wire it up

```csharp
builder.Services.AddOnafriqPayments(builder.Configuration);
```

Validates `ApiKey` and `MerchantId` at registration.

## `PaymentMethodToken` semantics

For collections (`ProcessPaymentAsync`): the payer wallet, formatted `"<country>:<walletNumber>"`. If no `:` is present, country defaults to `ZA`.

Example: `"ZA:27710000000"` — South Africa wallet `27710000000`.

## `PayoutRequest.DestinationToken` format

Same format as `PaymentMethodToken`: `"<country>:<walletNumber>"`. If no `:` is present, country defaults to `GH`.

Example: `"GH:233244000000"` — Ghana wallet `233244000000`.

## Metadata

`Metadata` is not read by the provider — wallet routing is encoded in the token string.

## Settlement

**Asynchronous.** Both collections and transfers return `Pending` immediately; the final outcome arrives via webhook to `CallbackUrl`.

## Refunds

`ProcessRefundAsync` throws `BhenguPaymentException` with `ProviderErrorCode = "refund_unsupported"` — **Onafriq money movement is one-directional.** Reverse a transaction by issuing a new opposite payout from your merchant wallet back to the original payer's wallet.

## Payouts

**Yes — this is the primary capability.** `IPayoutProvider.ProcessPayoutAsync` calls `POST v1/transactions` (wallet-to-wallet transfer).

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `X-Signature` header.

```csharp
app.MapPost("/webhooks/onafriq", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Onafriq)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `transaction.completed`, `transaction.successful`, `transaction.failed`, `transaction.rejected`, `transaction.pending`, `collection.completed`, `collection.failed`.

## Capabilities

`Charge | Payout | Webhook | MobileMoney | CrossBorder`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
