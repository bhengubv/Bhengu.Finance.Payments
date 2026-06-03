# Bhengu.Finance.Payments.AirtelMoney

Airtel Money provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Countries: Kenya · Uganda · Tanzania · Zambia · Malawi · Madagascar · DRC · Nigeria · Niger · Chad · Rwanda · Republic of Congo · Gabon · Seychelles. Collect (charge), Disbursement (payout), and Refund via the Airtel Africa Open API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.AirtelMoney
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "AirtelMoney": {
          "ClientId": "...",
          "ClientSecret": "...",
          "Country": "KE",
          "Currency": "KES",
          "CallbackUrl": "https://yoursite.example/airtel/callback",
          "WebhookSecret": "...",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ClientId`, `ClientSecret` (OAuth2 against `auth/oauth2/token`), `Country` (ISO-3166 alpha-2, sent as `X-Country`), and `Currency` (ISO-4217, sent as `X-Currency`).

## Wire it up

```csharp
builder.Services.AddAirtelMoneyPayments(builder.Configuration);
```

Validates all four required options at registration. Access tokens cached until 60s before expiry.

## `PaymentMethodToken` semantics

**The payer's MSISDN** (international format, e.g. `254712345678` for Kenya). Missing MSISDN throws `PaymentDeclinedException` with `ProviderErrorCode = "missing_msisdn"`.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `transaction_id` | Optional | 16-char merchant txn id (defaults to GUID-derived) | `order-12345678` |
| `reference` | Optional | Free-text reference (defaults to `Description`) | `order-123` |

## `PayoutRequest.DestinationToken` format

Just the recipient's MSISDN (e.g. `254712345678`).

## Settlement

**Asynchronous.** Collect / Disbursement / Refund all return immediately with a status code (`TIP`/`TS`/`TF`/`TA`). The final outcome arrives via webhook callback.

## Refunds

Yes — `ProcessRefundAsync` calls `POST standard/v1/payments/refund` with the `airtel_money_id` from the original collection.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST standard/v1/disbursements/`.

## Webhook

HMAC-SHA256 of the body, base64-encoded, in the `X-Auth-Signature` header (`WebhookSecret`-keyed).

```csharp
app.MapPost("/airtel/callback", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.AirtelMoney)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Auth-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Status codes mapped: `TS` (success) → Completed, `TIP` (in progress) → Pending, `TF` (failed) → Failed, `TA` (cancelled/aborted) → Cancelled.

## Capabilities

`Charge | Refund | Payout | Webhook | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
