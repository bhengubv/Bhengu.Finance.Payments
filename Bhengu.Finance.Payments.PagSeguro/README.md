# Bhengu.Finance.Payments.PagSeguro

PagSeguro / PagBank (Brazil) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Cards, PIX, boleto, and wallet via the PagBank v4 REST API. PagBank models payments as **orders** with one-or-more **charges**.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PagSeguro
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PagSeguro": {
          "ApiToken": "...",
          "WebhookSecret": "...",
          "NotificationUrl": "https://yoursite.example/webhooks/pagbank",
          "Currency": "BRL",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ApiToken` (Bearer token on every request). `WebhookSecret` is HMAC-SHA256 secret.

## Wire it up

```csharp
builder.Services.AddPagSeguroPayments(builder.Configuration);
```

Validates `ApiToken` at registration.

## `PaymentMethodToken` semantics

For `payment_method_type=CREDIT_CARD`: a PagBank **encrypted card payload** (from PagBank's client-side encryption SDK) sent as `card.encrypted`. Ignored for PIX / boleto.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `payment_method_type` | Optional | `CREDIT_CARD`, `PIX`, `BOLETO` (defaults to `CREDIT_CARD`) | `PIX` |
| `installments` | Optional | Integer (defaults to 1) | `3` |
| `reference_id` | Optional | Merchant ref (defaults to `pagseguro-<guid>`) | `order-123` |
| `store_card` | Optional | `true`/`false` for card vaulting | `true` |
| `holder_name` | Optional | Cardholder name | `Maria Silva` |
| `customer_name` | Optional | Customer display name | `Maria Silva` |
| `customer_email` | Optional | E-mail | `buyer@example.com` |
| `customer_tax_id` | Optional | CPF/CNPJ | `12345678901` |

## `PayoutRequest.DestinationToken` format

Pipe-delimited bank account: `"branch|number|check_digit|holder_tax_id|holder_name|bank_code"`.

Example: `"0001|12345|6|12345678901|Maria Silva|033"` (Santander BR). Less than 6 segments throws `BhenguPaymentException` with `ProviderErrorCode = "invalid_destination_token"`.

## Settlement

**Synchronous** for cards (auto-captured) — `ProcessPaymentAsync` creates the order, captures the first charge, and returns `Completed`/`Failed`. PIX/boleto return `Pending`/`WAITING` until paid. Amounts sent in centavos (Amount × 100); `GatewayReference` is the PagBank order id.

## Refunds

Yes — `ProcessRefundAsync` calls `POST /charges/{chargeId}/cancel` with `amount.value` (centavos). **Note: `GatewayReference` for refunds is the CHARGE id, not the order id** — look it up from the order before refunding.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST /transfers` against the merchant balance.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `x-authenticity-token` header.

```csharp
app.MapPost("/webhooks/pagbank", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.PagSeguro)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["x-authenticity-token"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Status values mapped: `PAID`/`AUTHORIZED`/`CAPTURED` → Completed; `WAITING`/`IN_ANALYSIS`/`PENDING` → Pending; `DECLINED`/`FAILED` → Failed; `CANCELED`/`VOIDED` → Cancelled; `REFUNDED` → Refunded.

## Capabilities

`Charge | Refund | Payout | Webhook | Cards | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
