# Bhengu.Finance.Payments.Mukuru

Mukuru (South Africa outbound remittance) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

B2B remittance from South Africa to Zimbabwe, Malawi, Mozambique, Zambia, Ghana, Kenya, Uganda, Nigeria, Tanzania, Côte d'Ivoire — via cash pickup, mobile money, or bank transfer.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Mukuru
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Mukuru": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantId": "...",
          "WebhookSecret": "...",
          "SenderCountry": "ZA",
          "DefaultCurrency": "ZAR",
          "CallbackUrl": "https://yoursite.co.za/webhooks/mukuru",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ClientId` and `ClientSecret` (OAuth2 client_credentials). `WebhookSecret` is HMAC-SHA256 secret for callback verification.

## Wire it up

```csharp
builder.Services.AddMukuruPayments(builder.Configuration);
```

Validates `ClientId` and `ClientSecret` at registration. Tokens are minted lazily on first call and cached until 30s before expiry.

## `PaymentMethodToken` semantics

For `ProcessPaymentAsync` (wallet top-up): the merchant's funding reference. The actual money movement is `IPayoutProvider`-driven — `PaymentMethodToken` on the payment is just a tracking string.

## `PayoutRequest.DestinationToken` format

`"<recipientCountry>:<payoutMethod>:<accountOrMsisdn>[:bankCode]"`

Examples:
- `"ZW:CASH_PICKUP:"` — Zimbabwe cash collection
- `"MW:MOBILE_MONEY:265888123456"` — Malawi mobile money
- `"ZM:BANK:0123456789:ZNB"` — Zambia ZNB bank transfer

Invalid format throws `BhenguPaymentException` with `ProviderErrorCode = "invalid_destination"`.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `payment_method` | Optional | Top-up funding method (defaults to `EFT`) | `EFT`, `CARD` |

## Settlement

**Asynchronous.** Both wallet top-up and outbound transactions return `Pending` immediately; the real outcome lands via webhook to `CallbackUrl`.

## Refunds

`ProcessRefundAsync` calls Mukuru's `cancel-transaction` endpoint — valid **only before the recipient collects** the funds. After collection, refunds are not possible and Mukuru's API will return an error which surfaces as `PaymentDeclinedException`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` is the primary capability — it wraps `POST v1/transactions` (Create Transaction). See the `DestinationToken` format above.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `X-Mukuru-Signature` header (accepts `sha256=...` prefix).

```csharp
app.MapPost("/webhooks/mukuru", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Mukuru)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Mukuru-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `transaction.completed`, `transaction.paid`, `transaction.collected`, `transaction.pending`, `transaction.created`, `transaction.failed`, `transaction.rejected`, `transaction.cancelled`, `transaction.refunded`.

## Capabilities

`Charge | Refund | Payout | Webhook | CrossBorder | MobileMoney | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
