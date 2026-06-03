# Bhengu.Finance.Payments.Flutterwave

Flutterwave provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Pan-African aggregator (34+ countries) covering cards, bank transfers, mobile money (M-Pesa, MoMo, Airtel), and USSD. Wraps the Flutterwave v3 REST API.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Flutterwave
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Flutterwave": {
          "SecretKey": "FLWSECK_TEST-...",
          "PublicKey": "FLWPUBK_TEST-...",
          "EncryptionKey": "...",
          "WebhookSecret": "...",
          "RedirectUrl": "https://yoursite.example/flw/return"
        }
      }
    }
  }
}
```

Required: `SecretKey` (Bearer token on every request). `PublicKey` and `EncryptionKey` are for client-side card-tokenisation flows; `WebhookSecret` is the verbatim string Flutterwave echoes in the `verif-hash` header.

## Wire it up

```csharp
builder.Services.AddFlutterwavePayments(builder.Configuration);
```

Validates `SecretKey` at registration.

## `PaymentMethodToken` semantics

The merchant **transaction reference** (`tx_ref`) — Flutterwave's primary correlator. Use your order id or a generated UUID.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `email` | Required | E-mail | `buyer@example.com` |
| `name` | Optional | Display name (defaults to `email`) | `Thandi Bhengu` |
| `phonenumber` | Optional | E.164 | `+27821234567` |

Missing `email` throws `PaymentDeclinedException` with `ProviderErrorCode = "missing_email"`.

## `PayoutRequest.DestinationToken` format

`"<bankCode>:<accountNumber>"` (e.g. `"044:0690000040"` for Access Bank Nigeria).

Invalid format throws `PaymentDeclinedException` with `ProviderErrorCode = "invalid_destination"`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` initialises a hosted-payment session and returns `Pending` plus the checkout `RedirectUrl` (`data.link`). Real outcome arrives via webhook.

## Refunds

Yes — `ProcessRefundAsync` calls `POST v3/transactions/{transaction_id}/refund` with `amount`. The `GatewayReference` must be the **numeric Flutterwave transaction id** (not the `tx_ref`).

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `POST v3/transfers`.

## Webhook

**Flutterwave does NOT HMAC** — it echoes the configured `WebhookSecret` verbatim in the `verif-hash` header. The provider does a constant-time compare.

```csharp
app.MapPost("/webhooks/flutterwave", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Flutterwave)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["verif-hash"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `charge.completed`, `charge.complete`, `transfer.completed`, `charge.failed`, `transfer.failed`, `refund.completed`, `refund.created`.

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | Cards | MobileMoney | BankTransfer | CrossBorder`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
