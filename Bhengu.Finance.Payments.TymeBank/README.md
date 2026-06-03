# Bhengu.Finance.Payments.TymeBank

TymeBank (South Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Pay-by-bank instant transfers, Scan-to-Pay QR codes, and EFT/PayShap payouts against TymeBank's developer API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.TymeBank
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "TymeBank": {
          "ClientId": "...",
          "ClientSecret": "...",
          "MerchantId": "...",
          "WebhookSecret": "...",
          "CallbackUrl": "https://yoursite.co.za/webhooks/tyme",
          "Currency": "ZAR",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ClientId` and `ClientSecret` (OAuth2 client_credentials against `oauth2/token`).

## Wire it up

```csharp
builder.Services.AddTymeBankPayments(builder.Configuration);
```

Validates `ClientId` and `ClientSecret` at registration. Access tokens cached until 30s before expiry.

## `PaymentMethodToken` semantics

The merchant **payment reference** stamped on the bank transfer (becomes the `reference` field for instant payment or the `merchant_ref` for QR generation).

## Metadata keys

`mode` switches between instant pay-by-bank (default) and Scan-to-Pay QR.

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `mode` | Optional | `instant` (default) or `qr` | `qr` |
| `debtor_account` | Instant only | Account number | `1234567890` |
| `debtor_branch_code` | Instant only | Branch code | `678910` |
| `creditor_account` | Instant only | Account number | `9876543210` |
| `creditor_branch_code` | Instant only | Branch code | `678910` |
| `creditor_name` | Optional | Display name (defaults to `Description`) | `Vendor Co.` |
| `expiry_minutes` | QR only | Integer (defaults to 10) | `30` |

## `PayoutRequest.DestinationToken` format

`"<bankCode>:<accountNumber>[:<beneficiaryName>]"`

Example: `"250655:1234567890:Jane Doe"`. Invalid format throws `BhenguPaymentException`.

## Settlement

**Synchronous** for instant pay-by-bank — `ProcessPaymentAsync` returns `Completed`, `Pending`, or `Failed` directly. QR generation returns `Pending` plus the QR string in the response `Message`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST v1/payments/{paymentId}/refund` with `amount` and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST v1/payouts` (EFT / PayShap, depending on TymeBank account setup).

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in `X-Tyme-Signature` (accepts `sha256=...` prefix).

```csharp
app.MapPost("/webhooks/tyme", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.TymeBank)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Tyme-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `payment.completed`, `payment.succeeded`, `payment.pending`, `payment.failed`, `payment.cancelled`, `payment.refunded`, `payout.completed`, `payout.pending`, `payout.failed`, `refund.completed`.

## Capabilities

`Charge | Refund | Payout | Webhook | SyncSettlement | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
