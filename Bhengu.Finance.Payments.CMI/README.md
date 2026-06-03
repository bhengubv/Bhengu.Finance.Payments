# Bhengu.Finance.Payments.CMI

CMI (Centre Monétique Interbancaire / Morocco) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Morocco's interbank 3D Secure card gateway, based on the Garanti BBVA POS XML protocol. Redirect-only checkout.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.CMI
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "CMI": {
          "ClientId": "...",
          "StoreKey": "...",
          "ApiUser": "...",
          "ApiPassword": "...",
          "OkUrl": "https://yoursite.example/cmi/ok",
          "FailUrl": "https://yoursite.example/cmi/fail",
          "CallbackUrl": "https://yoursite.example/webhooks/cmi",
          "Currency": "504",
          "Lang": "en",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ClientId` (CMI merchant clientid) and `StoreKey` (used in SHA-512 hash construction). `ApiUser` + `ApiPassword` are required for the XML CC5 refund/inquiry flow. Currency is **numeric ISO-4217** (`504` = MAD, `840` = USD, `978` = EUR).

## Wire it up

```csharp
builder.Services.AddCMIPayments(builder.Configuration);
```

Validates `ClientId` and `StoreKey` at registration.

## `PaymentMethodToken` semantics

The merchant **order id** (`oid`). CMI has no card tokens — the caller chooses `oid` and must keep it unique. Defaults to `cmi-<guid>` if empty.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `email` | Optional | E-mail | `buyer@example.com` |
| `BillToName` | Optional | Cardholder name | `Hassan Alami` |
| `rnd` | Optional | Random nonce (defaults to `DateTime.UtcNow.Ticks`) | `1730000000` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` does NOT call CMI — it builds a signed redirect URL to `<base>/fim/est3Dgate?...&hash=<sha512-base64>` and returns `Pending`. The customer is redirected to CMI's 3D Secure page; the issuer authentication outcome arrives via async callback to `CallbackUrl` plus a same-window redirect to `OkUrl`/`FailUrl`.

Hash construction (hashAlgorithm=ver3): pipe-join all sorted field values with `\` and `|` escaping, append `StoreKey`, SHA-512, base64.

## Refunds

Yes — `ProcessRefundAsync` POSTs a `CC5Request` XML envelope (Type=`Credit`) to `/fim/api` and returns Refunded when CMI's `Response` field equals `Approved`.

## Payouts

**Not supported.** CMI is a card-acquiring gateway only; the provider does NOT implement `IPayoutProvider`.

## Webhook

SHA-512 base64 of `<sorted form-urlencoded payload> + StoreKey`. The caller must pre-construct the canonical input string (the SDK doesn't unpack the inbound form on your behalf).

```csharp
app.MapPost("/webhooks/cmi", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.CMI)] IPaymentGatewayProvider provider) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var hash = form["HASH"].ToString();

    // Reconstruct canonical input per CMI spec.
    var canonical = BuildCmiCanonical(form);
    if (!provider.VerifyWebhookSignature(canonical, hash))
        return Results.Unauthorized();

    // ParseWebhookAsync expects the form-urlencoded body string.
    var rawBody = string.Join('&', form.Select(kv => $"{kv.Key}={kv.Value}"));
    var evt = await provider.ParseWebhookAsync(rawBody);
    return Results.Ok();
});
```

Callback fields mapped: `Response=Approved` + `ProcReturnCode=00` → Completed; `Response=Declined`/`Error` → Failed; `mdStatus=1` (3DS full auth) → Completed.

## Capabilities

`Charge | Refund | Webhook | RedirectFlow | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
