# Bhengu.Finance.Payments.Alipay

Alipay+ Cross-Border provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Ant Group's global merchant API for accepting Chinese consumers (1B+ users) and disbursing payouts. RSA-SHA256 signed.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Alipay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Alipay": {
          "ClientId": "...",
          "MerchantPrivateKey": "MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQ...",
          "AlipayPublicKey": "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAr...",
          "NotifyUrl": "https://yoursite.example/webhooks/alipay",
          "RedirectUrl": "https://yoursite.example/alipay/return",
          "Currency": "USD",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ClientId` and `MerchantPrivateKey` (PEM or base64 body — both accepted). `AlipayPublicKey` is required for webhook signature verification.

## Wire it up

```csharp
builder.Services.AddAlipayPayments(builder.Configuration);
```

Validates `ClientId` and `MerchantPrivateKey` at registration.

## `PaymentMethodToken` semantics

The merchant **`paymentRequestId`** — your idempotent reference for this charge attempt. The provider sends it verbatim and Alipay returns its own `paymentId` as `GatewayReference`.

## Metadata

`Metadata` is not currently read by the provider. The request body is mostly fixed (`paymentMethodType=ALIPAY_CN`, `productCode=CASHIER_PAYMENT`); per-request metadata is forwarded only if you extend the provider.

## `PayoutRequest.DestinationToken` format

The recipient's **Alipay account number** (`beneficiaryAccountNo`). The provider hard-codes `beneficiaryBankCode=ALIPAY` for the standard Alipay-wallet payout.

## Settlement

**Synchronous-ish.** `ProcessPaymentAsync` returns the Alipay+ `paymentId` plus either a `normalUrl` (cashier redirect) or a status. Outcome may be `Pending` and finalised via webhook. Amounts sent in **minor units** (Amount × 100) as strings.

## Refunds

Yes — `ProcessRefundAsync` calls `POST /ams/api/v1/payments/refund` with `refundRequestId`, `paymentId`, `refundAmount`, `refundReason`, and `refundNotifyUrl`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST /ams/api/v1/payments/payout`.

## Webhook

**RSA-SHA256** of the raw JSON body, base64-encoded, in the `signature` header (format `algorithm=RSA256,keyVersion=1,signature=<b64>` — extract the `signature=` portion). Verified with `AlipayPublicKey`.

```csharp
app.MapPost("/webhooks/alipay", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Alipay)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var sigHeader = ctx.Request.Headers["signature"].ToString();
    var base64Sig = ExtractB64Signature(sigHeader);  // strip "algorithm=RSA256,keyVersion=1,signature="

    if (!provider.VerifyWebhookSignature(body, base64Sig))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised notify types: `PAYMENT_RESULT`, `REFUND_RESULT`, `PAYOUT_RESULT`. Result codes mapped: `SUCCESS`/`PAYMENT_SUCCESS` → Completed; `PROCESSING` → Pending; `FAILED`/`FAIL`/`PAYMENT_FAIL` → Failed; `CANCELLED` → Cancelled.

## Capabilities

`Charge | Refund | Payout | Webhook | CrossBorder | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
