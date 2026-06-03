# Bhengu.Finance.Payments.Fawry

Fawry (Egypt) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Egypt's largest payment network (PayAtFawry retail outlets, MWALLET, cards) via the Fawry ECommerce REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Fawry
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Fawry": {
          "MerchantCode": "...",
          "SecurityKey": "...",
          "DefaultPaymentMethod": "CARD",
          "ReturnUrl": "https://yoursite.example/fawry/return",
          "NotificationUrl": "https://yoursite.example/webhooks/fawry",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `MerchantCode` and `SecurityKey` (SHA-256 hash key for request signing and webhook verification).

## Wire it up

```csharp
builder.Services.AddFawryPayments(builder.Configuration);
```

Validates `MerchantCode` and `SecurityKey` at registration.

## `PaymentMethodToken` semantics

**Unused for the redirect flow.** Pass any merchant string or empty — Fawry uses `merchantRefNum` (from metadata) as the primary correlator.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `merchantRefNum` | Optional | Merchant ref (defaults to `fawry-<guid>`) | `order-123` |
| `customerProfileId` | Optional | Customer id | `cust-42` |
| `customerName` | Optional | Display name | `Thandi Bhengu` |
| `customerMobile` | Optional | Phone number | `201001234567` |
| `customerEmail` | Optional | E-mail | `buyer@example.com` |
| `paymentMethod` | Optional | `CARD`, `MWALLET`, `PAYATFAWRY` (defaults to `DefaultPaymentMethod`) | `PAYATFAWRY` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` calls `payments/charge` and returns Fawry's reference. For `PAYATFAWRY` / `MWALLET` the payer completes payment out-of-band (at a retail outlet or via wallet app) — the real outcome arrives via webhook. Fawry `statusCode 12000` means request accepted; the `orderStatus` field carries the lifecycle state (`NEW`, `PAID`, `DELIVERED`, `EXPIRED`, `REFUNDED`, `CANCELED`). Amounts formatted as `"0.00"`.

## Refunds

Yes — `ProcessRefundAsync` calls `POST payments/refund` with `referenceNumber`, `refundAmount`, and a SHA-256 signature over the canonical fields.

## Payouts

**Not supported.** Fawry's standard merchant API doesn't expose payouts; the provider does NOT implement `IPayoutProvider`. Use the separate Fawry Disbursement product.

## Webhook

SHA-256 hex of `<canonical-payload> + SecurityKey`. The caller **must** construct the canonical input from notification fields in Fawry's documented order: `fawryRefNumber + merchantRefNum + paymentAmount + orderAmount + orderStatus + paymentMethod + paymentReferenceNumber`.

```csharp
app.MapPost("/webhooks/fawry", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Fawry)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var rawBody = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["signature"].ToString();

    // Build the canonical input from notification fields per Fawry's spec.
    var canonical = BuildFawryCanonical(rawBody);
    if (!provider.VerifyWebhookSignature(canonical, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(rawBody);
    return Results.Ok();
});
```

OrderStatus values mapped: `PAID`/`DELIVERED` → Completed; `NEW`/`PENDING` → Pending; `REFUNDED` → Refunded; `EXPIRED`/`FAILED` → Failed; `CANCELED` → Cancelled.

## Capabilities

`Charge | Refund | Webhook | RedirectFlow | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
