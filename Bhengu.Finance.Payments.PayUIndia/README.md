# Bhengu.Finance.Payments.PayUIndia

PayU India provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Cards, UPI, netbanking, EMI, BNPL via the PayU India hosted-page redirect plus the info-service JSON API for refunds and payouts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayUIndia
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PayUIndia": {
          "MerchantKey": "...",
          "Salt": "...",
          "SuccessUrl": "https://yoursite.example/payu/success",
          "FailureUrl": "https://yoursite.example/payu/failure",
          "Currency": "INR",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `MerchantKey` and `Salt` (used in SHA-512 hash construction for every signed call and webhook).

## Wire it up

```csharp
builder.Services.AddPayUIndiaPayments(builder.Configuration);
```

Validates `MerchantKey` and `Salt` at registration.

## `PaymentMethodToken` semantics

**Unused for the redirect flow.** PayU India identifies the transaction by merchant `txnid` (metadata) plus the SHA-512 hash.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `txnid` | Optional | Merchant txn id (defaults to `txn-<guid>`) | `order-123` |
| `firstname` | Optional | Given name (defaults to `Customer`) | `Rahul` |
| `email` | Optional | E-mail (defaults to `buyer@example.com`) | `buyer@example.com` |
| `phone` | Optional | Phone number | `+919876543210` |
| `udf1` ... `udf5` | Optional | User-defined free-text passthrough | `coupon-50` |
| `surl` | Optional | Success redirect URL (overrides `SuccessUrl` option) | `https://...` |
| `furl` | Optional | Failure redirect URL (overrides `FailureUrl` option) | `https://...` |

## `PayoutRequest.DestinationToken` format

The PayU India **payee identifier** (account number / VPA / beneficiary id) passed verbatim as `var1` in the `fund_transfer` command.

## Settlement

**Asynchronous (redirect).** `ProcessPaymentAsync` does NOT call PayU — it builds the hosted-page redirect URL with the `key|txnid|amount|productinfo|firstname|email|udf1...5|||||salt` SHA-512 hash and returns `Pending` plus the `RedirectUrl`. The customer is sent to `<host>/_payment?...`; PayU posts the result S2S to your `surl`/`furl`.

Amounts formatted as `"0.00"`.

## Refunds

Yes — `ProcessRefundAsync` POSTs `command=cancel_refund_transaction` to `merchant/postservice.php?form=2` (info-service) with `var1=paymentId`, `var2=tokenId`, `var3=amount` and a SHA-512 hash.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` issues `command=fund_transfer` against the info-service endpoint.

## Webhook

SHA-512 hex over the **caller-reconstructed canonical input**: `salt|status|||||udf5|udf4|udf3|udf2|udf1|email|firstname|productinfo|amount|txnid|key`. The caller must build that string from the S2S response body before invoking `VerifyWebhookSignature`.

```csharp
app.MapPost("/webhooks/payu", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.PayUIndia)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();  // typically form-urlencoded

    var hash = ExtractHashField(body);
    var canonical = BuildPayUCanonicalInput(body);  // per spec above
    if (!provider.VerifyWebhookSignature(canonical, hash))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);  // accepts form or JSON
    return Results.Ok();
});
```

Status values mapped: `success`/`captured` → Completed; `pending`/`in progress` → Pending; `failed`/`failure`/`dropped`/`bounced` → Failed; `user_cancelled`/`cancelled` → Cancelled; `refunded`/`queued_for_refund` → Refunded.

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
