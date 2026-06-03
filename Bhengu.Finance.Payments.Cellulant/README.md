# Bhengu.Finance.Payments.Cellulant

Cellulant (Tingg / Mula) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Pan-African aggregator (Tingg Checkout for collections, Mula for disbursements) covering 35+ countries. Single integration for cards, mobile money, and bank transfers.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Cellulant
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Cellulant": {
          "ServiceCode": "BIL-...",
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantTransactionId": "tgn",
          "CallbackUrl": "https://yoursite.example/webhooks/tingg",
          "WebhookSecret": "...",
          "CountryCode": "KE",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ServiceCode` (Tingg merchant service identifier), `ClientId` and `ClientSecret` (OAuth2 client_credentials against `v1/oauth/token/request`).

## Wire it up

```csharp
builder.Services.AddCellulantPayments(builder.Configuration);
```

Validates the three required options at registration. Access tokens cached until 60s before expiry.

## `PaymentMethodToken` semantics

**The payer's MSISDN** (international format). The provider uses it for both `msisdn` and `accountNumber` / `payerClientCode` in the Tingg Express body.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `email` | Optional | E-mail (defaults to `noreply@example.com`) | `buyer@example.com` |
| `name` | Optional | Display name (defaults to `Customer`) | `Thandi Bhengu` |

## `PayoutRequest.DestinationToken` format

Just the recipient's MSISDN (e.g. `254712345678`) — used directly as `destinationMSISDN`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending` plus a Tingg hosted-checkout `RedirectUrl`. The payer completes payment via mobile-money STK, card, or bank — final outcome arrives via webhook.

## Refunds

Yes — `ProcessRefundAsync` calls `POST refunds` with `transactionId`, `amount`, and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST disbursement/v1/initiate` (Mula).

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `x-tingg-signature` header.

```csharp
app.MapPost("/webhooks/tingg", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Cellulant)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["x-tingg-signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `payment.success`, `checkout.success`, `payment.failed`, `checkout.failed`, `refund.success`, `refund.processed`, `disbursement.success`, `disbursement.failed`.

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | MobileMoney | CrossBorder`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
