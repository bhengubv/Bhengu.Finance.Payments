# Bhengu.Finance.Payments.Hubtel

Hubtel (Ghana) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Hosted checkout, refunds, and mobile-money send-money (payout) for Ghana via the Hubtel REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Hubtel
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Hubtel": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantAccountNumber": "POS-...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://yoursite.example/webhooks/hubtel",
          "ReturnUrl": "https://yoursite.example/hubtel/return",
          "Currency": "GHS",
          "UseSandbox": false
        }
      }
    }
  }
}
```

Required: `ClientId` and `ClientSecret` (HTTP Basic-auth), and `MerchantAccountNumber` (Hubtel POS id).

## Wire it up

```csharp
builder.Services.AddHubtelPayments(builder.Configuration);
```

Validates all three required options at registration.

## `PaymentMethodToken` semantics

The merchant **clientReference** stamped on the checkout — your order id or any merchant string.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `payeeName` | Optional | Display name | `Thandi Bhengu` |
| `payeeMobileNumber` | Optional | Phone number | `233244000000` |
| `payeeEmail` | Optional | E-mail | `buyer@example.com` |

## `PayoutRequest.DestinationToken` format

`"<channel>:<msisdn>"` where channel is one of `mtn-gh`, `vodafone-gh`, `tigo-gh`.

Example: `"mtn-gh:233244000000"`. Missing `:` throws `BhenguPaymentException`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` initiates a checkout and returns `Pending` plus the `CheckoutUrl` as `RedirectUrl`. Final outcome arrives via webhook to `CallbackUrl`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST transactions/refund` with `transactionId`, `amount`, `reason`, and a fresh `clientReference`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST merchantaccount/merchants/{accountNumber}/send/mobilemoney`.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `Signature` header.

```csharp
app.MapPost("/webhooks/hubtel", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Hubtel)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Status values mapped: `success`/`paid`/`completed` → Completed; `failed`/`declined` → Failed; `cancelled` → Cancelled; `refunded` → Refunded. Event types `refund.completed` and `payout.completed` are also recognised explicitly.

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | Cards | MobileMoney`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
