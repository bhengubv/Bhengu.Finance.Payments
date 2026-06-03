# Bhengu.Finance.Payments.Interswitch

Interswitch (Nigeria / pan-African) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Quickteller Pay and Disbursement REST APIs via Interswitch's OAuth2 Passport endpoint. Card payments, refunds, and bank disbursements for Nigeria's Verve / Mastercard / Visa rails.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Interswitch
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Interswitch": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantCode": "MX...",
          "ProductId": "...",
          "TerminalId": null,
          "WebhookSecret": "...",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ClientId` and `ClientSecret` (OAuth2 Passport client_credentials, Basic-auth on `passport/oauth/token`). `MerchantCode` and `ProductId` (PayItem code) are required for Quickteller flows.

## Wire it up

```csharp
builder.Services.AddInterswitchPayments(builder.Configuration);
```

Validates `ClientId` and `ClientSecret` at registration. Tokens cached and refreshed 30s before expiry. Every request also carries Interswitch's `Signature` / `SignatureMethod` / `Timestamp` / `Nonce` headers (SHA-512 hex of `clientId + method + resource + timestamp + nonce + secretKey`).

## `PaymentMethodToken` semantics

The **transfer code** (or tokenised card reference, depending on PayItem). Passed as `transferCode` in the advice body.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `requestReference` | Optional | Merchant ref (defaults to `isw-<guid>`) | `order-123` |
| `customerEmail` | Optional | E-mail (defaults to `noreply@bhengu.example`) | `buyer@example.com` |
| `customerId` | Optional | Customer identifier (defaults to `anonymous`) | `cust-42` |
| `mobileNo` | Optional | Phone number | `+2348012345678` |

## `PayoutRequest.DestinationToken` format

`"<bankCode>:<accountNumber>"` or just `"<accountNumber>"` (with bank code defaulted elsewhere). Bank code is the NIBSS short code (e.g. `058` GTBank).

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns the Interswitch `transactionRef` and a status based on the gateway `responseCode` (`00` = success). Amounts sent in kobo (Amount × 100). Final settlement details arrive via webhook.

## Refunds

Yes — `ProcessRefundAsync` calls `POST api/v2/quickteller/transactions/{transactionRef}/refund` with `amount` (in kobo) and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST api/v2/disbursements/transactions`.

## Webhook

HMAC-SHA512 of the body, hex-encoded lowercase, in the `X-Interswitch-Signature` header.

```csharp
app.MapPost("/webhooks/interswitch", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Interswitch)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Interswitch-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `payment.successful`, `transaction.successful`, `disbursement.successful`, `payment.failed`, `transaction.failed`, `disbursement.failed`, `refund.successful`, `refund.processed`.

## Capabilities

`Charge | Refund | Payout | Webhook | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
