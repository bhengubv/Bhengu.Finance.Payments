# Bhengu.Finance.Payments.OrangeMoney

Orange Money Web Payment provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Countries: Côte d'Ivoire · Senegal · Cameroon · Mali · Burkina Faso · Madagascar · Niger · Botswana · Sierra Leone · Guinea (Conakry & Bissau) · Liberia · DRC. Checkout-redirect flow via Orange's Developer Portal.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.OrangeMoney
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "OrangeMoney": {
          "ConsumerKey": "...",
          "ConsumerSecret": "...",
          "MerchantKey": "...",
          "Country": "ci",
          "ReturnUrl": "https://yoursite.example/orange/return",
          "CancelUrl": "https://yoursite.example/orange/cancel",
          "NotifUrl": "https://yoursite.example/orange/notif",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ConsumerKey`, `ConsumerSecret` (OAuth2 client_credentials, Basic-auth on `oauth/v2/token`), `MerchantKey` (issued by Orange Money, embedded in every Web Payment body), and `Country` (two-letter path segment: `ci`, `sn`, `cm`, `ml`, …).

## Wire it up

```csharp
builder.Services.AddOrangeMoneyPayments(builder.Configuration);
```

Validates all four required options at registration.

## `PaymentMethodToken` semantics

**Unused.** Orange Money Web Payment doesn't tokenise — the payer chooses their MSISDN on Orange's hosted page. The SDK returns `RedirectUrl`; you send the payer there.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `order_id` | Optional | Merchant order id (defaults to generated 20-char) | `order-123` |
| `lang` | Optional | Two-letter language code (defaults to `fr`) | `en`, `fr` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending` plus a `RedirectUrl` (Orange's hosted payment page). The real outcome arrives via the `notif` POST to `NotifUrl`.

Amount is rounded to the nearest integer (Orange Money Web Payment uses whole-unit amounts in XOF/XAF/MGA etc).

## Refunds

`ProcessRefundAsync` throws `BhenguPaymentException` — **Orange Money Web Payment has no automated refund API.** Process the reversal manually via the Orange Money merchant portal.

## Payouts

**Not supported.** Orange Money Web Payment doesn't expose disbursements; the provider does NOT implement `IPayoutProvider`.

## Webhook

**Orange Money does NOT cryptographically sign callbacks.** Pass the persisted `notif_token` (returned in the original webpayment response) as the `signature` argument; the provider compares it in constant time against the `notif_token` field in the inbound payload.

```csharp
app.MapPost("/orange/notif", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.OrangeMoney)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    // notifToken was stored in your DB alongside the order when ProcessPaymentAsync ran.
    var notifToken = await db.GetNotifTokenForPayToken(...);
    if (!provider.VerifyWebhookSignature(body, notifToken))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

You **must** persist `notif_token` from the `OrangeWebPaymentResponse` alongside your order — the SDK doesn't surface it through `PaymentResponse` (use the provider's underlying API if you need it directly).

## Capabilities

`Charge | Webhook | RedirectFlow | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
