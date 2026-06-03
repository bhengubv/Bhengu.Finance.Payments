# Bhengu.Finance.Payments.Pesapal

Pesapal (East Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Countries: Kenya · Uganda · Tanzania · Rwanda · Zambia · Malawi. Hosted-payment-page checkout (cards, M-Pesa, Airtel Money, EFT) via Pesapal API 3.0.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Pesapal
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Pesapal": {
          "ConsumerKey": "...",
          "ConsumerSecret": "...",
          "IpnId": "...",
          "CallbackUrl": "https://yoursite.example/pesapal/return",
          "IpnUrl": "https://yoursite.example/webhooks/pesapal",
          "Currency": "KES",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ConsumerKey` and `ConsumerSecret` (used to obtain a 5-min Bearer token via `api/Auth/RequestToken`). `IpnId` is required on `ProcessPaymentAsync` — obtain it once via `api/URLSetup/RegisterIPN`.

## Wire it up

```csharp
builder.Services.AddPesapalPayments(builder.Configuration);
```

Validates `ConsumerKey` and `ConsumerSecret` at registration. Access tokens refreshed every 4.5 minutes.

## `PaymentMethodToken` semantics

The merchant **order `id`** sent on `SubmitOrderRequest` — your reference.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `email` | Optional | E-mail | `buyer@example.com` |
| `phone_number` | Optional | Phone number | `+254712345678` |
| `country_code` | Optional | ISO-3166 alpha-2 (defaults to `KE`) | `UG` |
| `first_name` | Optional | Given name | `Thandi` |
| `last_name` | Optional | Family name | `Bhengu` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending` plus the Pesapal hosted-page `RedirectUrl` and the `OrderTrackingId` as `GatewayReference`. Real outcome arrives via IPN to your registered IPN URL.

Missing `IpnId` throws `ProviderConfigurationException`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST api/Transactions/RefundRequest` with `confirmation_code`, `amount`, and `remarks`. Pesapal status `200` → Refunded; otherwise Failed.

## Payouts

**Not supported.** Pesapal doesn't expose payouts on the standard merchant API.

## Webhook

**Pesapal does NOT HMAC IPN payloads.** `VerifyWebhookSignature` does a constant-time comparison between the supplied signature and the configured `ConsumerSecret` — callers must source the signature from a trusted reverse-proxy header. **Production callers SHOULD additionally call `api/Transactions/GetTransactionStatus`** with the `OrderTrackingId`.

```csharp
app.MapPost("/webhooks/pesapal", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Pesapal)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null) return Results.Ok();

    // Confirm via GetTransactionStatus(evt.GatewayReference) before treating as authoritative.
    return Results.Ok();
});
```

The IPN body carries `OrderTrackingId` and `OrderNotificationType` — parsed events always have `Status = Pending` (canonical settlement state comes from `GetTransactionStatus`).

## Capabilities

`Charge | Refund | Webhook | RedirectFlow | Cards | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
