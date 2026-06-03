# Bhengu.Finance.Payments.MercadoPago

Mercado Pago (Latin America) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Argentina · Brazil · Chile · Colombia · Mexico · Peru · Uruguay · Venezuela. Cards, PIX, boleto, and Mercado wallet via the Mercado Pago REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.MercadoPago
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "MercadoPago": {
          "AccessToken": "TEST-...",
          "PublicKey": "TEST-...",
          "WebhookSecret": "...",
          "NotificationUrl": "https://yoursite.example/webhooks/mp",
          "Currency": "BRL"
        }
      }
    }
  }
}
```

Required: `AccessToken` (production prefix `APP_USR-`, test prefix `TEST-`; used as the Bearer token). `PublicKey` is for client-side Checkout Bricks tokenisation; `WebhookSecret` verifies the `x-signature` HMAC.

## Wire it up

```csharp
builder.Services.AddMercadoPagoPayments(builder.Configuration);
```

Validates `AccessToken` at registration.

## `PaymentMethodToken` semantics

A **Mercado Pago card token** (`tkn_...`) from Checkout Bricks for card payments. **Ignored** for PIX (`payment_method_id=pix`) and boleto (`payment_method_id=bol*`) — those flows don't need a token.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `payment_method_id` | Optional | `visa`, `master`, `pix`, `bolbradesco`, ... (defaults to `visa`) | `pix` |
| `payer_email` / `email` | Required | E-mail | `buyer@example.com` |
| `first_name` | Optional | Given name | `Maria` |
| `last_name` | Optional | Family name | `Silva` |
| `identification_type` | Optional | Tax ID type (defaults to `CPF`) | `CNPJ` |
| `identification_number` | Optional | Tax ID number | `12345678901` |
| `installments` | Optional | Integer (defaults to 1) | `12` |

Missing `payer_email`/`email` throws `PaymentDeclinedException` with `ProviderErrorCode = "missing_payer_email"`.

## `PayoutRequest.DestinationToken` format

The recipient's Mercado Pago **payee email** (used as `payee.email` in the money-request body).

## Settlement

**Synchronous-ish.** Cards typically return `Completed` or `Failed`; PIX returns `Pending` plus QR code data (in `point_of_interaction.transaction_data` — currently surfaced via the raw response, not the SDK's `Message` field). Final state arrives via webhook. Every POST is sent with an `X-Idempotency-Key`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST /v1/payments/{paymentId}/refunds` with `amount` (full refund if omitted, but the SDK always sends it).

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST /v1/money_requests`.

## Webhook

HMAC-SHA256 with the `x-signature` header (format `ts=<unix>,v1=<sig>`). The caller must construct the **canonical manifest** Mercado Pago documents — `id:<data.id>;request-id:<x-request-id>;ts:<ts>;` — before invoking `VerifyWebhookSignature`.

```csharp
app.MapPost("/webhooks/mercadopago", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.MercadoPago)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var sigHeader = ctx.Request.Headers["x-signature"].ToString();
    var reqId = ctx.Request.Headers["x-request-id"].ToString();

    var manifest = BuildMpManifest(body, reqId, sigHeader);  // id:..;request-id:..;ts:..;
    if (!provider.VerifyWebhookSignature(manifest, sigHeader))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `payment.created`/`payment.updated` → Pending; `payment.approved` → Completed; `payment.failed`/`payment.rejected` → Failed; `payment.cancelled` → Cancelled; any `refund` type → Refunded.

## Capabilities

`Charge | Refund | Payout | Webhook | Cards | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
