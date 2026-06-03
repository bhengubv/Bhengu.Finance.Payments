# Bhengu.Finance.Payments.ExpressPay

ExpressPay (Ghana, Gambia, Sierra Leone, Liberia, Nigeria) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Hosted-page card and mobile-money checkout via the ExpressPay form-encoded `submit.php` / `query.php` API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.ExpressPay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "ExpressPay": {
          "MerchantId": "...",
          "ApiKey": "...",
          "RedirectUrl": "https://yoursite.example/expresspay/return",
          "PostUrl": "https://yoursite.example/webhooks/expresspay",
          "Currency": "GHS",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `MerchantId` and `ApiKey`. `PostUrl` is the server-to-server result callback.

## Wire it up

```csharp
builder.Services.AddExpressPayPayments(builder.Configuration);
```

Validates `MerchantId` and `ApiKey` at registration.

## `PaymentMethodToken` semantics

The merchant **order-id** sent verbatim on `submit.php` — your reference.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `accountnumber` | Optional | Account number | `0241234567` |
| `username` | Optional | Username | `thandi` |
| `email` | Optional | E-mail | `buyer@example.com` |
| `firstname` | Optional | Given name | `Thandi` |
| `lastname` | Optional | Family name | `Bhengu` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` POSTs `submit.php` form-encoded and returns `Pending` plus the hosted-page `payment_url` as `RedirectUrl` (when `status == 1`). Final outcome arrives via callback to `PostUrl`.

## Refunds

`ProcessRefundAsync` throws `BhenguPaymentException` with `ProviderErrorCode = "not_supported"` — **ExpressPay has no refund API.** Issue refunds via the ExpressPay merchant portal.

## Payouts

**Not supported.** The provider does NOT implement `IPayoutProvider`.

## Webhook

**ExpressPay does NOT HMAC the post-url callback.** `VerifyWebhookSignature` does a constant-time comparison between the supplied signature and the configured `ApiKey` — callers must source the signature from a trusted reverse-proxy header. **Production callers SHOULD additionally call `QueryStatusAsync(token)`** to confirm authenticity:

```csharp
app.MapPost("/webhooks/expresspay", async (HttpContext ctx,
    ExpressPayPaymentProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    // Parse either JSON or form-encoded body.
    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null) return Results.Ok();

    // Confirm authenticity by querying status server-to-server.
    var status = await provider.QueryStatusAsync(evt.GatewayReference);
    return Results.Ok();
});
```

Inject the concrete `ExpressPayPaymentProvider` (not just `IPaymentGatewayProvider`) to use `QueryStatusAsync`.

Status values mapped: `1` → Completed, `2` → Pending, `3` → Failed, `4` → Cancelled.

## Capabilities

`Charge | Webhook | RedirectFlow | Cards | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
