# Bhengu.Finance.Payments.Remita

Remita (SystemSpecs / Nigeria) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Nigerian government revenue collection, corporate e-collection, and Single Send Money payouts. Authentication uses SHA-512 hex hashes of concatenated fields with the API key — **no bearer tokens**.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Remita
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Remita": {
          "MerchantId": "...",
          "ServiceTypeId": "...",
          "ApiKey": "...",
          "ApiToken": "...",
          "FromBank": "058",
          "DebitAccount": "0123456789",
          "Currency": "NGN",
          "CallbackUrl": "https://yoursite.example/webhooks/remita",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `MerchantId` (the `remitaConsumerKey`), `ServiceTypeId` (collection service), and `ApiKey`. Payouts additionally require `FromBank` and `DebitAccount`.

## Wire it up

```csharp
builder.Services.AddRemitaPayments(builder.Configuration);
```

Validates `MerchantId`, `ServiceTypeId`, and `ApiKey` at registration.

## `PaymentMethodToken` semantics

The merchant **orderId** — Remita's primary correlator for payment-init. Passed as `orderId` in the body and hashed into the SHA-512 auth construction.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `payerName` | Optional | Display name (defaults to `Bhengu Customer`) | `Thandi Bhengu` |
| `payerEmail` | Optional | E-mail (defaults to `noreply@bhengu.local`) | `payer@example.com` |
| `payerPhone` | Optional | Phone number | `+2348012345678` |

## `PayoutRequest.DestinationToken` format

`"<creditBankCode>:<creditAccountNumber>"` (e.g. `"058:0123456789"` for GTBank). Invalid format throws `BhenguPaymentException` with `ProviderErrorCode = "invalid_destination"`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns the Remita **RRR** (Remita Retrieval Reference) as `GatewayReference`, status mapped from `statuscode` (`025` = PaymentInitiated → Pending, `00`/`01` → Pending, `0`/`success` → Completed, `020` → Failed). Customers complete payment via Remita's hosted page or bank channels.

## Refunds

Yes — `ProcessRefundAsync` calls `remita/refundservice/refund/initiate` with `merchantId`, `rrr`, `amount`, `reason`, and a SHA-512 hash.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls Single Send Money against the configured `FromBank` / `DebitAccount`. Throws `ProviderConfigurationException` if those aren't set.

## Webhook

SHA-512 of `(rrr + status + apiKey)`, hex-encoded. The provider parses the JSON body to extract `rrr` and `status` and compares against the supplied signature.

```csharp
app.MapPost("/webhooks/remita", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Remita)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var hash = ctx.Request.Headers["remitaHash"].ToString();

    if (!provider.VerifyWebhookSignature(body, hash))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised status codes: `00`/`01`/`success`/`successful`/`completed` → Completed; `021`/`025`/`pending` → Pending; `020`/`failed`/`declined` → Failed; `refunded` → Refunded.

## Capabilities

`Charge | Refund | Payout | Webhook | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
