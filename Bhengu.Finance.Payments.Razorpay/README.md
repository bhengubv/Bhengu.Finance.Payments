# Bhengu.Finance.Payments.Razorpay

Razorpay (India) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

India's most popular gateway. Cards, UPI, netbanking, EMI, wallets via the Razorpay REST API. Server-side capture of pre-authorised payments, refunds, and RazorpayX payouts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Razorpay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Razorpay": {
          "KeyId": "rzp_test_...",
          "KeySecret": "...",
          "WebhookSecret": "...",
          "RazorpayXAccountNumber": "2323230012345678",
          "Currency": "INR"
        }
      }
    }
  }
}
```

Required: `KeyId` and `KeySecret` (Basic-auth on every request). `RazorpayXAccountNumber` is the virtual account funding payouts (required for `IPayoutProvider`).

## Wire it up

```csharp
builder.Services.AddRazorpayPayments(builder.Configuration);
```

Validates `KeyId` and `KeySecret` at registration.

## `PaymentMethodToken` semantics

A **Razorpay `payment_id`** (`pay_...`) returned by the client-side Razorpay Checkout. The provider issues `POST v1/payments/{paymentId}/capture` to settle a pre-authorised payment.

To use the **Orders flow** instead (where the SDK creates the order and the customer is redirected to checkout with the `order_id`), set `metadata["flow"] = "order"` — the SDK calls `POST v1/orders` and returns the order id as `GatewayReference`.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `flow` | Optional | `order` to use the Orders flow; omit for direct capture | `order` |
| `receipt` | Optional | Merchant receipt (defaults to `rcpt_<guid>`) | `order-123` |

Full `Metadata` is forwarded as `notes` on the order.

## `PayoutRequest.DestinationToken` format

A RazorpayX **`fund_account_id`** (`fa_...`) — create fund accounts beforehand via `/v1/fund_accounts`. The SDK posts an IMPS payout against `RazorpayXAccountNumber`.

## Settlement

**Synchronous** for capture flow — `ProcessPaymentAsync` returns `Completed` or `Failed` directly after capture. Orders flow returns `Pending` plus the order id. Amounts sent in paise (Amount × 100).

## Refunds

Yes — `ProcessRefundAsync` calls `POST v1/payments/{paymentId}/refund` with `amount` (paise), `speed=normal`, and `notes.reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST v1/payouts` against the RazorpayX virtual account (IMPS). Missing `RazorpayXAccountNumber` throws `ProviderConfigurationException`.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in the `X-Razorpay-Signature` header.

```csharp
app.MapPost("/webhooks/razorpay", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Razorpay)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Razorpay-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `payment.captured`, `payment.authorized`, `order.paid` → Completed; `payment.failed` → Failed; `refund.created`, `refund.processed` → Refunded; `payout.processed` → Completed.

## Capabilities

`Charge | Refund | Payout | Webhook | Cards | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
