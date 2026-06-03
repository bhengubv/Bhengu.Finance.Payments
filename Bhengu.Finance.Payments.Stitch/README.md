# Bhengu.Finance.Payments.Stitch

Stitch (South Africa open-banking) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Pay-by-bank, InstantEFT, LinkPay, and bank-to-bank payouts via the Stitch GraphQL API. Covers FNB, ABSA, Standard Bank, Nedbank, Capitec, Investec, and Discovery.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Stitch
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Stitch": {
          "ClientId": "test-...",
          "ApiKey": "sk_test_...",
          "WebhookSecret": "...",
          "BeneficiaryAccountNumber": "1234567890",
          "BeneficiaryBankId": "absa",
          "BeneficiaryName": "Acme Pty Ltd",
          "Currency": "ZAR",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ClientId` plus either `ApiKey` (X-API-Key header) **or** `ClientAssertionJwt` (X-Client-Assertion for the full OAuth2 flow). Payments additionally require `BeneficiaryAccountNumber`, `BeneficiaryBankId`, and `BeneficiaryName`.

## Wire it up

```csharp
builder.Services.AddStitchPayments(builder.Configuration);
```

Validates `ClientId` and at least one auth credential at registration.

## `PaymentMethodToken` semantics

Used as the **merchant external reference** stamped on the payment initiation request — also the default for `payer_reference` and `beneficiary_reference` when not supplied via metadata.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `payer_reference` | Optional | Defaults to `PaymentMethodToken` | `order-123` |
| `beneficiary_reference` | Optional | Defaults to `PaymentMethodToken` | `Acme #123` |

## `PayoutRequest.DestinationToken` format

`"<bankId>:<accountNumber>:<beneficiaryName>"` (all three required).

Example: `"capitec:1234567890:Jane Doe"`. Invalid format throws `BhenguPaymentException` with `ProviderErrorCode = "invalid_destination"`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` issues a `clientPaymentInitiationRequestCreate` GraphQL mutation and returns `Pending` plus a `RedirectUrl` (the consumer's bank authorisation page). The real outcome arrives via webhook.

## Refunds

Yes — `ProcessRefundAsync` calls the REST endpoint `POST api/v1/payments/{paymentId}/refund` with `amount` and `reason`.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` issues a `clientPayoutInitiationRequestCreate` GraphQL mutation.

## Webhook

HMAC-SHA256 of the body, hex-encoded lowercase, in `X-Stitch-Signature` (accepts `sha256=...` prefix).

```csharp
app.MapPost("/webhooks/stitch", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Stitch)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Stitch-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Recognised event types: `paymentInitiationRequest.completed`, `payment.completed`, `payment.settled`, `paymentInitiationRequest.pending`, `payment.pending`, `paymentInitiationRequest.failed`, `payment.failed`, `payment.rejected`, `paymentInitiationRequest.cancelled`, `payment.refunded`, `refund.completed`.

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | BankTransfer`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
