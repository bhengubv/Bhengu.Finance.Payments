# Bhengu.Finance.Payments.Slydepay

Slydepay (Ghana) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Mobile-first wallet checkout for Ghana via the legacy `paymentservice.asmx` JSON API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Slydepay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Slydepay": {
          "EmailOrMobile": "merchant@example.com",
          "MerchantKey": "...",
          "Currency": "GHS",
          "PaymentChannels": "7",
          "CallbackUrl": "https://yoursite.example/webhooks/slydepay",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `EmailOrMobile` (the Slydepay-registered merchant identity) and `MerchantKey`. `PaymentChannels` is a bitmask: `1` = card, `2` = mobile, `4` = wallet, `7` = all.

## Wire it up

```csharp
builder.Services.AddSlydepayPayments(builder.Configuration);
```

Validates `EmailOrMobile` and `MerchantKey` at registration.

## `PaymentMethodToken` semantics

The merchant **orderCode** stamped on the transaction — your reference, also required for follow-up `VerifyTransactionStatus` / `CancelTransactionStatus` calls.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `comment1` | Optional | Free-text | `Order #123` |
| `comment2` | Optional | Free-text | `Promo: APR-50` |
| `surcharge` | Optional | Decimal string (defaults to `0`) | `1.50` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` calls `ProcessPaymentOrder` and returns `Pending` plus the `CheckOutUrl` as `RedirectUrl` (when `success == true`). Final outcome arrives via callback to `CallbackUrl`.

## Refunds

`ProcessRefundAsync` throws `BhenguPaymentException` with `ProviderErrorCode = "not_supported"` — **Slydepay has no native refund API.** Issue refunds via the Slydepay merchant portal.

## Payouts

**Not supported.** The provider does NOT implement `IPayoutProvider`.

## Webhook

**Slydepay does NOT HMAC** its `PaymentNotificationUrl` callbacks. `VerifyWebhookSignature` does a constant-time comparison between the supplied signature and the configured `MerchantKey`. **Production callers SHOULD additionally re-confirm via `VerifyTransactionAsync(payToken, orderCode)`**:

```csharp
app.MapPost("/webhooks/slydepay", async (HttpContext ctx,
    SlydepayPaymentProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null) return Results.Ok();

    // Confirm authenticity by calling VerifyTransactionStatus.
    var verifyBody = await provider.VerifyTransactionAsync(evt.GatewayReference, "order-123");
    return Results.Ok();
});
```

## Provider-specific extras

Inject the concrete `SlydepayPaymentProvider` to use:
- `VerifyTransactionAsync(payToken, orderCode)` — call `VerifyTransactionStatus`
- `CancelTransactionAsync(payToken, orderCode)` — call `CancelTransactionStatus`

## Capabilities

`Charge | Webhook | RedirectFlow | Cards | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
