# Bhengu.Finance.Payments.OPay

OPay (Nigeria / Egypt / Pakistan) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

OPay International cashier (hosted checkout), refund, and payout via the OPay REST API. Cards plus OPay-wallet plus bank rails.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.OPay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "OPay": {
          "PublicKey": "OPAYPUB...",
          "SecretKey": "OPAYPRV...",
          "MerchantId": "...",
          "Country": "NG",
          "CallbackUrl": "https://yoursite.example/webhooks/opay",
          "ReturnUrl": "https://yoursite.example/opay/return",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `PublicKey`, `SecretKey` (HMAC-SHA512 signs every request body, sent in `Authorization: Bearer <sig>`), `MerchantId` (the OPay "sn" short name).

## Wire it up

```csharp
builder.Services.AddOPayPayments(builder.Configuration);
```

Validates the three required options at registration.

## `PaymentMethodToken` semantics

The **OPay `payMethod`** (e.g. `BankCard`, `Account`, `BankTransfer`, `Wallet`) — controls which option is preselected on OPay's hosted cashier.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `reference` | Optional | Merchant ref (defaults to `opay-<guid>`) | `order-123` |
| `userId` | Optional | Customer id (defaults to `anonymous`) | `cust-42` |
| `userEmail` | Optional | E-mail (defaults to `noreply@bhengu.example`) | `buyer@example.com` |
| `userMobile` | Optional | Phone number | `+2348012345678` |
| `userName` | Optional | Display name (defaults to `Bhengu Customer`) | `Thandi Bhengu` |

## `PayoutRequest.DestinationToken` format

The recipient identifier OPay routes to — the OPay account number or wallet ID, passed verbatim as `receiver.receiverId`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` creates a cashier session and returns `Pending` plus a `RedirectUrl` (the hosted checkout). Real outcome arrives via webhook. OPay envelope code `00000` = success; anything else maps to `Failed`. Amounts sent in smallest currency unit (Amount × 100).

## Refunds

Yes — `ProcessRefundAsync` calls `POST api/v1/international/refund/create` with `orderNo`, `refundAmount`, and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST api/v1/international/payout/create`.

## Webhook

HMAC-SHA512 of the body, hex-encoded lowercase, in `Authorization` (accepts `Bearer ` prefix).

```csharp
app.MapPost("/webhooks/opay", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.OPay)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["Authorization"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `transaction.success`, `payment.success`, `transaction.failed`, `payment.failed`, `refund.success`, `refund.completed`. Falls back to the embedded `payload.status` (`success`, `failed`, `refunded`).

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | Cards | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
