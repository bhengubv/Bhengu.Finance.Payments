# Bhengu.Finance.Payments.DPO

DPO Group (Network International) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Pan-African card gateway (20+ countries) with redirect-flow checkout via the DPO v6 Direct API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.DPO
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "DPO": {
          "CompanyToken": "...",
          "ServiceType": "3854",
          "ServiceDescription": "Online order",
          "RedirectUrl": "https://yoursite.example/dpo/return",
          "BackUrl": "https://yoursite.example/dpo/cancel",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `CompanyToken` (sent in the body of every authenticated request). `ServiceType` is DPO's product code (e.g. `3854`).

## Wire it up

```csharp
builder.Services.AddDPOPayments(builder.Configuration);
```

Validates `CompanyToken` at registration.

## `PaymentMethodToken` semantics

The merchant **company reference** (`CompanyRef`) — your order id or any merchant string. DPO does NOT tokenise the payer's card via this flow; card details are captured on DPO's hosted page.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `email` | Optional | E-mail | `buyer@example.com` |
| `firstName` | Optional | Given name | `Thandi` |
| `lastName` | Optional | Family name | `Bhengu` |

## Settlement

**Asynchronous.** `ProcessPaymentAsync` calls `createToken` and returns `Pending` plus the `TransToken`. Direct the customer to `https://secure.3gdirectpay.com/payv3.php?ID=<TransToken>` to complete payment. If DPO returns a non-`000` `Result`, the SDK throws `PaymentDeclinedException` with that code.

## Refunds

Yes — `ProcessRefundAsync` calls `refundToken` with `TransactionToken`, `refundAmount`, and `refundDetails`. A non-`000` result returns a `RefundResponse` with `Status = Failed` (does not throw).

## Payouts

**Not supported.** DPO doesn't expose payouts on the standard merchant tier; the provider does NOT implement `IPayoutProvider`.

## Webhook

**DPO does NOT sign callbacks.** Authenticity must be established by calling `verifyToken` against the `TransID` from the callback (out of band — the SDK doesn't currently wrap `verifyToken`). `VerifyWebhookSignature` returns `true` for any non-empty signature and logs a warning.

```csharp
app.MapPost("/webhooks/dpo", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.DPO)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    // DPO's production callbacks are form-encoded — marshal to JSON before calling ParseWebhookAsync.
    var evt = await provider.ParseWebhookAsync(body);
    // Then call verifyToken via DPO's API to confirm authenticity.
    return Results.Ok();
});
```

Recognised transaction-final-status values: `paid`, `approved`, `completed`, `declined`, `failed`, `cancelled`, `refunded`, `pending`.

## Capabilities

`Charge | Refund | Webhook | RedirectFlow | Cards | CrossBorder`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
