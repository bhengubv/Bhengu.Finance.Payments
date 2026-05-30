# Bhengu.Finance.Payments.MPesa

M-Pesa (Safaricom Daraja) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

Countries: Kenya · Tanzania · Mozambique · DRC · Egypt · Ethiopia · Ghana. The single biggest African payment system by transaction volume.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.MPesa
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "MPesa": {
          "ConsumerKey": "...",
          "ConsumerSecret": "...",
          "BusinessShortCode": "174379",
          "Passkey": "...",
          "CallbackUrl": "https://yoursite.co.ke/mpesa/callback",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `ConsumerKey`, `ConsumerSecret`, `BusinessShortCode`, `Passkey`. Get these from https://developer.safaricom.co.ke/.

## Wire it up

```csharp
builder.Services.AddMPesaPayments(builder.Configuration);
```

## `PaymentMethodToken` semantics

**The payer's MSISDN (phone number) in international format**, e.g. `254712345678` — NOT a tokenised card or persistent identifier.

```csharp
var response = await provider.ProcessPaymentAsync(new PaymentRequest
{
    PaymentMethodToken = "254712345678",   // payer's phone
    Amount = 100.00m,
    Currency = "KES",
    Description = "Order #123"
});
```

The provider issues an STK Push — the payer's phone rings with a payment prompt; they enter their M-Pesa PIN to authorise.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` returns `Pending` with the `CheckoutRequestID` as `GatewayReference`. The real outcome arrives via the callback posted to `CallbackUrl`. Treat the immediate response as "STK Push sent to the payer's phone" and the callback as the source of truth.

## Refunds

Yes — M-Pesa supports **Transaction Reversal** (B2C). `ProcessRefundAsync` calls `mpesa/reversal/v1/request` with the original transaction ID.

## Payouts

Yes — `IPayoutProvider` is implemented via M-Pesa B2C (Business-to-Customer). Use it to disburse to merchants, refunds, payroll.

```csharp
var payoutProvider = sp.GetRequiredKeyedService<IPayoutProvider>(ProviderNames.MPesa);
var payout = await payoutProvider.ProcessPayoutAsync(new PayoutRequest
{
    DestinationToken = "254712345678",   // recipient's phone
    Amount = 500.00m,
    Currency = "KES",
    Description = "Vendor settlement"
});
```

## Webhook callback

M-Pesa posts JSON callbacks (NOT signed cryptographically — Safaricom doesn't sign):

```jsonc
{
  "Body": {
    "stkCallback": {
      "MerchantRequestID": "...",
      "CheckoutRequestID": "...",
      "ResultCode": 0,                  // 0 = success
      "ResultDesc": "...",
      "CallbackMetadata": { "Item": [...] }
    }
  }
}
```

`VerifyWebhookSignature` does best-effort URL-token validation (since M-Pesa doesn't HMAC). **Always combine with origin-IP allowlisting from Safaricom's published ranges** for production.

```csharp
app.MapPost("/mpesa/callback", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.MPesa)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    // M-Pesa doesn't sign — VerifyWebhookSignature is best-effort.
    // Always validate the source IP against Safaricom's published range.
    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null) return Results.Ok();

    // Update your DB with evt.GatewayReference + evt.Status.
    return Results.Ok(); // Safaricom expects "C2B00011" for accepted, "C2B00012" to retry
});
```

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
