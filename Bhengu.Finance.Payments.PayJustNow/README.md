# Bhengu.Finance.Payments.PayJustNow

PayJustNow (South Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Buy-Now-Pay-Later: 3 interest-free instalments over 60 days for South African consumers.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayJustNow
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PayJustNow": {
          "ApiKey": "...",
          "SecretKey": "...",
          "MerchantId": "...",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ApiKey` (X-Api-Key header) and `MerchantId` (X-Merchant-Id header). `SecretKey` is required only for webhook signature verification.

## Wire it up

```csharp
builder.Services.AddPayJustNowPayments(builder.Configuration);
```

Validates `ApiKey` and `MerchantId` at registration.

## `PaymentMethodToken` semantics

The PayJustNow **customer token** for a previously-onboarded consumer (sent as the `customer_token` field on the order). For first-time customers, leave it empty — PayJustNow's hosted checkout collects the consumer details and returns a `RedirectUrl`.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `order_id` | Optional | Merchant ref (defaults to generated GUID) | `order-123` |
| `callback_url` | Optional | URL the order POSTs lifecycle events to | `https://yoursite.co.za/webhooks/pjn` |

The full `Metadata` dictionary is also forwarded on the order's `metadata` field.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` creates a BNPL order (status = `Pending`) and returns the `checkout_url` as `RedirectUrl`. The customer completes the agreement on PayJustNow's hosted page; the real outcome arrives via webhook.

## Refunds

Yes — `ProcessRefundAsync` calls `POST refunds` with `order_id`, `amount` (in cents), and `reason`. Whether a refund is honoured depends on the instalment lifecycle.

## Payouts

**Not supported.** PayJustNow does not implement `IPayoutProvider` — BNPL is a one-direction consumer-credit rail.

## Webhook

HMAC-SHA256 of the raw body using `SecretKey`, hex-encoded lowercase.

```csharp
app.MapPost("/webhooks/payjustnow", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.PayJustNow)] IPaymentGatewayProvider provider) =>
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

Recognised event types: `order.approved`, `order.completed`, `order.declined`, `order.cancelled`, `instalment.paid`, `instalment.overdue`, `instalment.failed`, `refund.approved`.

## Capabilities

`Charge | Refund | Webhook | RedirectFlow | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
