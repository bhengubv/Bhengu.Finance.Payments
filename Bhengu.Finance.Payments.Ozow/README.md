# Bhengu.Finance.Payments.Ozow

Ozow (South Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Instant EFT and PayShap pay-by-bank for South Africa via Ozow's hosted redirect flow.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Ozow
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Ozow": {
          "SiteCode": "ABC123",
          "PrivateKey": "...",
          "ApiKey": "...",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `SiteCode`, `PrivateKey` (used for SHA-512 hashes — request + webhook), `ApiKey` (sent on `ApiKey` header).

## Wire it up

```csharp
builder.Services.AddOzowPayments(builder.Configuration);
```

Validates `SiteCode`, `PrivateKey`, and `ApiKey` at registration.

## `PaymentMethodToken` semantics

**Unused for the redirect flow.** Ozow's instant-EFT model doesn't tokenise; if you pass a value it is logged for traceability only. Set it to `string.Empty` or any merchant reference.

## Metadata keys

The provider passes URLs and customer details from `Metadata` into the request body. All optional unless your Ozow site config requires them.

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `transaction_reference` | Optional | Merchant ref (defaults to a generated GUID) | `order-123` |
| `cancel_url` | Optional | URL | `https://yoursite.co.za/cancel` |
| `error_url` | Optional | URL | `https://yoursite.co.za/error` |
| `success_url` | Optional | URL | `https://yoursite.co.za/success` |
| `notify_url` | Optional | URL | `https://yoursite.co.za/webhooks/ozow` |
| `customer_first_name` | Optional | string | `Thandi` |
| `customer_last_name` | Optional | string | `Bhengu` |
| `customer_email` | Optional | email | `buyer@example.com` |
| `customer_phone` | Optional | E.164 | `+27821234567` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending` plus a `RedirectUrl` (Ozow's hosted page). Send the payer there; the real outcome arrives via webhook to your `notify_url`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST refund` with `siteCode`, `transactionId`, `amount`, and `reason`. Refund availability depends on your Ozow merchant agreement.

## Payouts

**Not supported.** Ozow's standard merchant API doesn't expose payouts; the provider does NOT implement `IPayoutProvider`. Merchants requiring disbursements should use Ozow's separate Disbursement API.

## Webhook

SHA-512 hex of `payload + PrivateKey`, lowercased, in the inbound notification body's `hash` field.

```csharp
app.MapPost("/webhooks/ozow", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Ozow)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var hash = ctx.Request.Form["Hash"].ToString();  // or pull from body if JSON

    if (!provider.VerifyWebhookSignature(body, hash))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

See `docs/WEBHOOKS.md` for the cross-provider webhook pattern.

## Capabilities

`Charge | Refund | Webhook | RedirectFlow | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
