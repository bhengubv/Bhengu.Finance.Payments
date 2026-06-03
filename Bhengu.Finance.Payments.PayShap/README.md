# Bhengu.Finance.Payments.PayShap

PayShap (South Africa) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

South Africa's real-time interbank rail (RTC) — instant ZAR account-to-account transfers using either explicit account numbers or proxy aliases (MSISDN / ID / e-mail).

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayShap
```

## Configuration

```json
{
  "PayShapSettings": {
    "ApiBaseUrl": "https://api.payshap.co.za",
    "ApiKey": "...",
    "ApiSecret": "...",
    "SignatureKey": "...",
    "MerchantId": "..."
  }
}
```

Note: PayShap binds from the root section name `PayShapSettings` (not the `Bhengu:Finance:Payments` namespace used by other providers) so the rich `IPayShapService` and the gateway adapter share one config.

## Wire it up

```csharp
builder.Services.AddPayShapServices(builder.Configuration);
```

Registers both `IPayShapService` (proxy resolution, account verification, EFT, multi-step settlement) and a `PayShapPaymentProvider` adapter that exposes PayShap via the generic `IPaymentGatewayProvider`.

## `PaymentMethodToken` semantics

Used as a **fallback proxy alias** for the payee — only consulted when `payshap.payee.identifier_value` metadata is absent. For account-to-account transfers the payee is fully specified via metadata (see below).

## Metadata keys

PayShap is a bank rail; the generic `PaymentRequest` doesn't carry everything it needs, so the adapter pulls it from `Metadata`. All keys below are read by `ProcessPaymentAsync`.

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `payshap.reference` | Optional | Merchant txn ref (defaults to GUID) | `order-123` |
| `payshap.payer.account` | Required | Account number | `1234567890` |
| `payshap.payer.bank_code` | Required | Bank code | `250655` |
| `payshap.payer.name` | Required | Display name | `Thandi Bhengu` |
| `payshap.payee.account` | Required | Account number | `9876543210` |
| `payshap.payee.bank_code` | Required | Bank code | `198765` |
| `payshap.payee.name` | Required | Display name | `Vendor Co.` |
| `payshap.payee.identifier_type` | Optional | `MSISDN`, `EMAIL`, `ID`, `BUSINESS`, `ACCOUNT` | `MSISDN` |
| `payshap.payee.identifier_value` | Optional | Proxy alias (defaults to `PaymentMethodToken`) | `+27821234567` |

Missing a required metadata key throws `PaymentDeclinedException` with `ProviderErrorCode = "missing_metadata"`.

## Settlement

**Synchronous.** PayShap is a real-time rail; `ProcessPaymentAsync` returns `Completed`, `Pending`, `Failed`, or `Cancelled` directly from the RTC response.

## Refunds

**Not supported as a concept.** `ProcessRefundAsync` throws `BhenguPaymentException` directing the caller to initiate a new RTC payment with payer and payee swapped — PayShap reversals are simply transfers in the opposite direction.

## Payouts

**Not supported via `IPayoutProvider`.** Use `ProcessPaymentAsync` with the merchant account as payer for outbound transfers, or use `IPayShapService` directly for richer disbursement flows.

## Webhook

HMAC-SHA256 via the `PayShapSignatureHelper`, lowercased hex, in the `X-PayShap-Signature` header (configurable). Verify with the SignatureKey.

```csharp
app.MapPost("/webhooks/payshap", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.PayShap)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-PayShap-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

## Provider-specific extras

For proxy resolution, account verification, multi-step settlement, and richer RTC operations, inject `IPayShapService` directly instead of the adapter.

## Capabilities

`Charge | Webhook | SyncSettlement | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
