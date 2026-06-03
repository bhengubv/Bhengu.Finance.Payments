# Bhengu.Finance.Payments.IPay

iPay (Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Kenya-centric pan-African gateway (Kenya · Uganda · Tanzania · Rwanda · DRC) covering cards, M-Pesa, Airtel Money, Equitel, and bank rails via iPay v3.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.IPay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "IPay": {
          "VendorId": "demo",
          "HashKey": "demoCHANGED",
          "Live": "1",
          "Currency": "KES",
          "CallbackUrl": "https://yoursite.example/webhooks/ipay",
          "UseSandbox": false
        }
      }
    }
  }
}
```

Required: `VendorId` (merchant code from iPay on-boarding) and `HashKey` (HMAC-SHA256 key). `Live` is the literal string `"1"` for live or `"0"` for test.

## Wire it up

```csharp
builder.Services.AddIPayPayments(builder.Configuration);
```

Validates `VendorId` and `HashKey` at registration.

## `PaymentMethodToken` semantics

The merchant **order id** (`oid`) — required, included in the SHA-256 HMAC hash and the redirect query string.

## Metadata keys

iPay v3 has a large surface — most fields map verbatim from metadata into the query string in the documented hash order.

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `inv` | Optional | Invoice number (defaults to `oid`) | `INV-123` |
| `tel` | Optional | Phone number | `254712345678` |
| `eml` | Optional | E-mail | `buyer@example.com` |
| `p1` / `p2` / `p3` / `p4` | Optional | Free-text passthrough fields | `coupon-50` |
| `cst` | Optional | Customer-style flag (defaults to `1`) | `1` |
| `crl` | Optional | Credit-line flag (defaults to `0`) | `0` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` does NOT call iPay — it constructs the hosted-payment-page redirect URL (with the HMAC-SHA256 hex hash over the concatenated fields) and returns `Pending` plus the `RedirectUrl`. Send the customer there. Real outcome arrives via callback to `CallbackUrl`.

## Refunds

`ProcessRefundAsync` throws `BhenguPaymentException` with `ProviderErrorCode = "not_supported"` — **iPay v3 has no refund API.** Issue refunds via the iPay merchant portal.

## Payouts

**Not supported.** The provider does NOT implement `IPayoutProvider`.

## Webhook

HMAC-SHA256 hex over the **caller-concatenated** payload (the caller must build the exact hash-input string in the iPay-documented field order before invoking `VerifyWebhookSignature`).

```csharp
app.MapPost("/webhooks/ipay", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.IPay)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var hash = ctx.Request.Headers["ipay-hash"].ToString();

    // Caller is responsible for re-constructing the canonical hash-input from the body fields.
    var canonicalInput = BuildIPayCanonical(body);
    if (!provider.VerifyWebhookSignature(canonicalInput, hash))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);  // accepts JSON or form-urlencoded
    return Results.Ok();
});
```

iPay status codes mapped: `aei7p7yrx4ae34` (success) → Completed; `bdi6p2yy76etrs`/`fe2707etr5s4wq`/`dtfi4p7yty45wq` → Failed.

## Provider-specific extras

Inject the concrete `IPayPaymentProvider` to use:
- `ChargeMpesaAsync(phone, amount, oid)` — direct M-Pesa C2B charge via the iPay mobile SDK endpoint

## Capabilities

`Charge | Webhook | RedirectFlow | Cards | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
