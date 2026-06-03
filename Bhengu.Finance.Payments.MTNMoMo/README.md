# Bhengu.Finance.Payments.MTNMoMo

MTN Mobile Money (MoMo) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Countries: Uganda · Ghana · Côte d'Ivoire · Cameroon · Zambia · Rwanda · Benin · Congo · Guinea · Liberia. MTN's Collection (RequestToPay) and Disbursement (Transfer) products via the MoMo Open API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.MTNMoMo
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "MTNMoMo": {
          "SubscriptionKey": "...",
          "ApiUserId": "...",
          "ApiKey": "...",
          "TargetEnvironment": "sandbox",
          "CallbackUrl": "https://yoursite.example/momo/callback",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `SubscriptionKey` (Ocp-Apim-Subscription-Key), `ApiUserId` UUID (created via the Provisioning API), `ApiKey` (paired with ApiUserId for Basic-auth on token exchange), and `TargetEnvironment` (`sandbox` for testing, or a market code in production: `mtnuganda`, `mtnghana`, `mtnivorycoast`, `mtncameroon`, `mtnzambia`, …). Get credentials from https://momodeveloper.mtn.com/.

## Wire it up

```csharp
builder.Services.AddMTNMoMoPayments(builder.Configuration);
```

Validates all four required options at registration. Access tokens are minted per product (`collection`, `disbursement`) and cached until 60s before expiry.

## `PaymentMethodToken` semantics

**The payer's MSISDN** (international format, e.g. `256777123456` for Uganda) — NOT a tokenised card or persistent identifier.

```csharp
var response = await provider.ProcessPaymentAsync(new PaymentRequest
{
    PaymentMethodToken = "256777123456",
    Amount = 100.00m,
    Currency = "UGX",
    Description = "Order #123"
});
```

Missing MSISDN throws `PaymentDeclinedException` with `ProviderErrorCode = "missing_msisdn"`.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `external_id` | Optional | Merchant correlator (defaults to the request's referenceId) | `order-123` |

## Settlement

**Asynchronous.** RequestToPay returns HTTP 202 — `ProcessPaymentAsync` returns `Pending` with the MoMo `ReferenceId` as `GatewayReference`. The real outcome arrives via callback to `CallbackUrl`, or by polling the status endpoint.

## Refunds

`ProcessRefundAsync` throws `BhenguPaymentException` — **MoMo has no refund API**. Reverse a collection by issuing a Disbursement Transfer to the original payer's MSISDN via `ProcessPayoutAsync`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` issues a Disbursement Transfer to the recipient's MSISDN.

```csharp
var payout = await payoutProvider.ProcessPayoutAsync(new PayoutRequest
{
    DestinationToken = "256777123456",
    Amount = 500.00m,
    Currency = "UGX",
    Description = "Vendor settlement"
});
```

## Webhook

**MoMo does NOT cryptographically sign callbacks.** `VerifyWebhookSignature` returns `false` and logs a warning. Authenticity relies on (a) the callback URL being unguessable, and (b) matching the `externalId` in the body against a known transaction.

```csharp
app.MapPost("/momo/callback", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.MTNMoMo)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    // No cryptographic signature — match evt.GatewayReference / externalId against your DB.
    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

## Capabilities

`Charge | Payout | Webhook | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
