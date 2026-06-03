# Bhengu.Finance.Payments.Kashier

Kashier (Egypt / UAE / KSA) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Server-to-server card charges, marketplace payouts, and hosted-payment-page redirect via the Kashier REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Kashier
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Kashier": {
          "ApiKey": "...",
          "MerchantId": "MID-...",
          "SecretKey": "...",
          "WebhookSecret": "...",
          "Currency": "EGP",
          "Mode": "test",
          "RedirectUrl": "https://yoursite.example/kashier/return",
          "ServerWebhookUrl": "https://yoursite.example/webhooks/kashier",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ApiKey` (sent as the literal `Authorization` header) and `MerchantId`. `SecretKey` is used to sign hosted-page redirect URLs; `WebhookSecret` is HMAC-SHA256 for webhook verification (falls back to `SecretKey`).

## Wire it up

```csharp
builder.Services.AddKashierPayments(builder.Configuration);
```

Validates `ApiKey` and `MerchantId` at registration.

## `PaymentMethodToken` semantics

A **Kashier card token** (`cardData` from the Kashier hosted page / iframe SDK) sent as `cardData` on the server-to-server charge.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `orderId` | Optional | Merchant order id (defaults to `kashier-<guid>`) | `order-123` |
| `shopperReference` | Optional | Persistent customer id | `cust-42` |

## `PayoutRequest.DestinationToken` format

A Kashier **destination identifier** (wallet, bank account, or marketplace seller id) passed verbatim as `destination`.

## Settlement

**Synchronous** for server-to-server charges with a card token — `ProcessPaymentAsync` returns `Completed`/`Pending`/`Failed` directly. Amounts formatted as `"0.00"`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST payments/refund` with `merchantId`, `orderId`/`transactionId`, `amount`, and `reason`. Successful refunds are normalised to `Status = Refunded`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST payouts` for marketplace disbursements.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `x-kashier-signature` header.

```csharp
app.MapPost("/webhooks/kashier", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Kashier)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["x-kashier-signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `PAY`/`CAPTURE` (Completed when `data.status == SUCCESS`), `REFUND` → Refunded, `VOID` → Cancelled, `FAILED` → Failed.

## Provider-specific extras

Inject the concrete `KashierPaymentProvider` for:
- `BuildHostedPaymentUrl(orderId, amount, currency)` — generate a signed redirect URL for the Kashier hosted page (computes the SHA-256 hash from `merchantId.orderId.amount.currency` + `SecretKey`)

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
