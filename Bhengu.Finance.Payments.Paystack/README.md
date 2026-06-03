# Bhengu.Finance.Payments.Paystack

Paystack (Nigeria / Ghana / South Africa / Kenya / Côte d'Ivoire / Egypt) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Server-to-server card charges, transfers (payouts), and refunds via the Paystack REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Paystack
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Paystack": {
          "SecretKey": "sk_test_...",
          "WebhookSecret": "...",
          "DefaultEmail": "noreply@yoursite.example"
        }
      }
    }
  }
}
```

Required: `SecretKey` (Bearer token on every request). `WebhookSecret` is HMAC-SHA512 secret — Paystack permits reusing `SecretKey` here, but a dedicated secret is recommended.

## Wire it up

```csharp
builder.Services.AddPaystackPayments(builder.Configuration);
```

Validates `SecretKey` at registration.

## `PaymentMethodToken` semantics

A **Paystack `authorization_code`** (typically `AUTH_...`) returned from a prior tokenisation (Paystack Popup, Paystack Inline, or a successful first-time charge). Pass it directly:

```csharp
var response = await provider.ProcessPaymentAsync(new PaymentRequest
{
    PaymentMethodToken = "AUTH_abc123",
    Amount = 99.99m,
    Currency = "NGN",
    Description = "Order #123",
    Metadata = new Dictionary<string, string> { ["email"] = "buyer@example.com" }
});
```

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `email` | Required (or `DefaultEmail` set in options) | E-mail | `buyer@example.com` |

The full `Metadata` dictionary is also forwarded on the charge's `metadata` field.

Missing email (with no `DefaultEmail`) throws `PaymentDeclinedException` with `ProviderErrorCode = "missing_email"`.

## `PayoutRequest.DestinationToken` format

A Paystack **transfer recipient code** (`RCP_...`). The provider strips an optional `recipient-` prefix. Create recipients beforehand via Paystack's `/transferrecipient` endpoint.

## Settlement

**Synchronous** for `charge_authorization` on existing tokens — `ProcessPaymentAsync` returns `Completed` or `Failed` directly. Amounts are sent in the smallest currency unit (kobo for NGN, cents for ZAR).

## Refunds

Yes — `ProcessRefundAsync` calls `POST refund` with `transaction` (the original reference) and `amount` (in smallest unit).

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST transfer` against the merchant balance.

## Webhook

HMAC-SHA512 of the body, hex-encoded lowercase, in the `x-paystack-signature` header.

```csharp
app.MapPost("/webhooks/paystack", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Paystack)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["x-paystack-signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `charge.success`, `transfer.success`, `charge.failed`, `transfer.failed`, `refund.processed`, `refund.processing`, `refund.created`.

## Capabilities

`Charge | Refund | Payout | Webhook | SyncSettlement | Cards | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
