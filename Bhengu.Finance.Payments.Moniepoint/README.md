# Bhengu.Finance.Payments.Moniepoint

Moniepoint (Nigeria) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Nigeria's largest agent-banking network. Wraps the Moniepoint REST API for hosted checkout initialisation, refunds, and inter-bank transfers.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Moniepoint
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Moniepoint": {
          "ApiKey": "mpt_...",
          "WebhookSecret": "...",
          "MerchantId": "...",
          "RedirectUrl": "https://yoursite.example/moniepoint/return",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ApiKey` (Bearer token on every request). `WebhookSecret` is HMAC-SHA512 secret — falls back to `ApiKey` if not set.

## Wire it up

```csharp
builder.Services.AddMoniepointPayments(builder.Configuration);
```

Validates `ApiKey` at registration.

## `PaymentMethodToken` semantics

The **payment method** the checkout should preselect — e.g. `card`, `bank_transfer`, `ussd`. Passed verbatim as `paymentMethod` in the initialise body.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `reference` | Optional | Merchant ref (defaults to `mpt-<guid>`) | `order-123` |
| `email` | Optional | E-mail (defaults to `noreply@bhengu.example`) | `buyer@example.com` |
| `name` | Optional | Display name (defaults to `Bhengu Customer`) | `Thandi Bhengu` |
| `phone` | Optional | Phone number | `+2348012345678` |

## `PayoutRequest.DestinationToken` format

`"<bankCode>:<accountNumber>"` or just `"<accountNumber>"` (bank defaults to empty — provide via the format).

## Settlement

**Asynchronous.** `ProcessPaymentAsync` initialises a transaction and returns `Pending` plus the reference; the payer is sent to the hosted checkout URL. Real outcome arrives via webhook.

## Refunds

Yes — `ProcessRefundAsync` calls `POST api/v1/transactions/{reference}/refund` with `amount` and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST api/v1/transfers`.

## Webhook

HMAC-SHA512 of the body, hex-encoded lowercase, in the `x-moniepoint-signature` header.

```csharp
app.MapPost("/webhooks/moniepoint", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Moniepoint)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["x-moniepoint-signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `transaction.successful`, `transfer.successful`, `transaction.failed`, `transfer.failed`, `refund.successful`, `refund.processed`.

## Capabilities

`Charge | Refund | Payout | Webhook | Cards | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
