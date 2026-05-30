# Bhengu.Finance.Payments.PayFast

PayFast (South Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayFast
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PayFast": {
          "MerchantId": "10000100",
          "MerchantKey": "46f0cd694581a",
          "Passphrase": "jt7NOE43FZPn",
          "UseSandbox": true,
          "ReturnUrl": "https://yoursite.co.za/payfast/return",
          "CancelUrl": "https://yoursite.co.za/payfast/cancel",
          "NotifyUrl": "https://yoursite.co.za/payfast/itn"
        }
      }
    }
  }
}
```

## Wire it up

```csharp
builder.Services.AddPayFastPayments(builder.Configuration);
```

Validates `MerchantId` is set at registration. Registers the provider keyed by `ProviderNames.PayFast`, and a startup-validation hosted service.

## `PaymentMethodToken` semantics

PayFast's ad-hoc subscription token (the `pf_token` returned after a customer completes a `subscription_type=2` agreement on PayFast's hosted page). Obtain it by sending the customer through `PayFastFormBuilder.BuildTokenisationUrl()` first.

```csharp
var formBuilder = sp.GetRequiredService<PayFastFormBuilder>();
var tokenisationUrl = formBuilder.BuildTokenisationUrl(
    returnUrl: "https://yoursite.co.za/onboarded",
    cancelUrl: "https://yoursite.co.za/cancelled",
    notifyUrl: "https://yoursite.co.za/payfast/itn");
// Redirect customer to tokenisationUrl; on success, PayFast posts the token back via ITN.
```

## Metadata keys read

- `payment_id` *(optional)* — merchant payment reference (set on `m_payment_id`)
- `transaction_id` *(optional, alternative)* — same purpose

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending`; the real outcome arrives via the ITN webhook posted to your `NotifyUrl`. Treat the immediate response as "accepted for processing" and the webhook as the source of truth.

## Refunds

PayFast doesn't expose a refund API — `ProcessRefundAsync` returns a manual tracking reference with `Status = Pending`. Process the refund manually in the PayFast merchant dashboard, then update your DB with the manual tracking ref.

## Webhook (ITN)

PayFast posts form-urlencoded ITN data to `NotifyUrl`. The signature is in the `signature` form field — extract it before calling `VerifyWebhookSignature`:

```csharp
app.MapPost("/webhooks/payfast", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var parsed = QueryHelpers.ParseQuery(body); // body is form-urlencoded
    var signature = parsed.GetValueOrDefault("signature").ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    // ... act on evt.GatewayReference + evt.Status
    return Results.Ok();
});
```

## Browser-redirect flow (separate from IPaymentGatewayProvider)

For first-time customers (no token yet) use `PayFastFormBuilder` which is registered alongside the provider:

```csharp
var formBuilder = sp.GetRequiredService<PayFastFormBuilder>();
var redirectUrl = formBuilder.BuildOnceOffPaymentUrl(
    mPaymentId: orderId.ToString(),
    amount: 99.99m,
    itemName: "Order #123",
    emailAddress: "buyer@example.com");
// Redirect customer to redirectUrl.
```

## Provider-specific extras

`PayFastPaymentProvider` exposes server-to-server methods beyond the generic interface:
- `FetchTokenAsync(token)` — get a tokenisation agreement's status
- `CancelTokenAsync(token)` — cancel an ad-hoc subscription
- `QueryTransactionAsync(txnOrPaymentId)` — server-side transaction lookup

Inject the concrete `PayFastPaymentProvider` (not just `IPaymentGatewayProvider`) to use these.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
