# Bhengu.Finance.Payments.Yoco

Yoco (South Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Server-to-server card charges and refunds via Yoco's Online REST API. South Africa's largest local-merchant card acquirer.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Yoco
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Yoco": {
          "SecretKey": "sk_test_...",
          "WebhookSecret": "whsec_..."
        }
      }
    }
  }
}
```

Required: `SecretKey` (Bearer token on every request).

## Wire it up

```csharp
builder.Services.AddYocoPayments(builder.Configuration);
```

Validates `SecretKey` at registration. Registers the provider keyed by `ProviderNames.Yoco`, plus startup validation.

## `PaymentMethodToken` semantics

A **Yoco card token** (e.g. `tok_visa_...`) produced by client-side tokenisation in Yoco's Web/Mobile SDK. Pass it directly:

```csharp
var response = await provider.ProcessPaymentAsync(new PaymentRequest
{
    PaymentMethodToken = "tok_visa_...",
    Amount = 99.99m,
    Currency = "ZAR",
    Description = "Order #123"
});
```

The provider POSTs to `charges/` with `amountInCents` (Amount × 100) and the token.

## Metadata

All keys in `PaymentRequest.Metadata` are forwarded verbatim on the `metadata` field of the charge — visible in the Yoco dashboard.

## Settlement

**Synchronous.** `ProcessPaymentAsync` returns `Completed`, `Pending`, or `Failed` directly from the HTTP response. The webhook is still your source of truth for any async state changes (disputes, refunds processed out-of-band).

## Refunds

Yes — `ProcessRefundAsync` calls `POST refunds/` with `chargeId` and `amountInCents`.

## Payouts

**Not supported.** Yoco's standard merchant API doesn't expose payouts; the provider intentionally does NOT implement `IPayoutProvider`. Merchants needing payouts should use Yoco Business / Marketplace tooling outside this SDK.

## Webhook

HMAC-SHA256 of the raw body using `WebhookSecret`, base64-encoded, supplied in the `Yoco-Signature` header.

```csharp
app.MapPost("/webhooks/yoco", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Yoco)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["Yoco-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `payment.succeeded`, `payment.failed`, `refund.succeeded`. Other event types return `null` from `ParseWebhookAsync`.

## Capabilities

`Charge | Refund | Webhook | SyncSettlement | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
