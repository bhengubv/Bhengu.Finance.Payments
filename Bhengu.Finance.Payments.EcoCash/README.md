# Bhengu.Finance.Payments.EcoCash

EcoCash (Zimbabwe) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Zimbabwe's dominant mobile-money operator. C2B instant charges and refunds via the EcoCash Developers v2 REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.EcoCash
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "EcoCash": {
          "ApiKey": "...",
          "Username": "...",
          "Password": "...",
          "MerchantCode": "...",
          "MerchantPin": "...",
          "MerchantNumber": "263771234567",
          "NotifyUrl": "https://yoursite.example/ecocash/callback",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ApiKey` (X-Api-Key header) and `MerchantCode`. `Username`/`Password` add Basic-auth on top of the API key. `MerchantPin` and `MerchantNumber` are embedded in every C2B body.

## Wire it up

```csharp
builder.Services.AddEcoCashPayments(builder.Configuration);
```

Validates `ApiKey` and `MerchantCode` at registration.

## `PaymentMethodToken` semantics

**The payer's MSISDN** (international format, e.g. `263771234567`) — passed as `endUserId` in the C2B body.

## Metadata

`Metadata` is not read by the provider. Use the configured options block for merchant identity.

## Settlement

**Asynchronous.** EcoCash sends an STK-style prompt to the payer's phone; `ProcessPaymentAsync` returns `Pending` (or whatever status the gateway returns immediately) plus the `ecocashReference` / `clientCorrelator`. The real outcome arrives via callback to `NotifyUrl`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST api/v2/payment/instant/refund` with the original `GatewayReference`. (Currency is hard-coded to `USD` in the refund body — EcoCash's standard refund flow.)

## Payouts

**Not supported.** EcoCash does NOT implement `IPayoutProvider`. Standard merchant tier is C2B only; bulk disbursement requires a separate EcoCash agreement.

## Webhook

**EcoCash does NOT sign callbacks.** Authenticity is established by (a) sending callbacks to a secret `NotifyUrl`, and (b) matching the `clientCorrelator` in the body against the value sent on the original charge. `VerifyWebhookSignature` returns `true` for any non-empty signature and logs a warning — callers should perform the correlator match in their webhook handler.

```csharp
app.MapPost("/ecocash/callback", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.EcoCash)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null) return Results.Ok();

    // Match evt.GatewayReference (ecocashReference or clientCorrelator) against your DB
    // before treating the event as authoritative.
    return Results.Ok();
});
```

## Capabilities

`Charge | Refund | Webhook | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
