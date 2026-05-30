# Bhengu.Finance.Payments.ApplePay

Apple Pay provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

## Important: Apple Pay tokenises, it doesn't settle

Apple Pay does NOT charge cards itself. It produces an encrypted PKPaymentToken that you forward to a real payment processor (Stripe, Adyen, Braintree, etc.). This provider:

1. Validates that the token is a well-formed PKPaymentToken JSON
2. Tags the request with `payment_source=apple_pay` metadata
3. Forwards the charge to the **DownstreamProcessor** named in config

If your downstream processor is `Stripe`, you must `AddStripePayments()` BEFORE `AddApplePayPayments()` — Apple Pay validates the downstream exists at app startup.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Stripe   # or Adyen, Braintree, etc.
dotnet add package Bhengu.Finance.Payments.ApplePay
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Stripe": {
          "SecretKey": "sk_test_..."
        },
        "ApplePay": {
          "MerchantId": "merchant.com.your-app",
          "DownstreamProcessor": "stripe",
          "DomainName": "yoursite.co.za"
        }
      }
    }
  }
}
```

## Wire it up

```csharp
builder.Services.AddStripePayments(builder.Configuration);     // downstream first
builder.Services.AddApplePayPayments(builder.Configuration);   // validates downstream at startup
```

**If you typo `"stripe"` as `"strpe"` or forget to register the downstream, the app crashes at startup** with a clear `ProviderConfigurationException` — not on the first inbound Apple Pay request.

## `PaymentMethodToken` semantics

A serialised **Apple Pay PKPaymentToken JSON** as supplied by your client app / web checkout. Shape:

```json
{
  "paymentData": { "version": "EC_v1", "data": "...", "signature": "...", "header": {...} },
  "paymentMethod": { "displayName": "Visa 1234", "network": "Visa", "type": "debit" },
  "transactionIdentifier": "..."
}
```

If the token isn't valid PKPaymentToken JSON, `ProcessPaymentAsync` throws `PaymentDeclinedException` with a specific `ProviderErrorCode` (`missing_token` / `invalid_token_json` / `invalid_token_shape`).

## Settlement

Inherits from the downstream processor. If Stripe is downstream, you get Stripe's synchronous settlement semantics.

## Refunds

Forwarded to the downstream processor by gateway reference.

## Webhooks

**Apple Pay has no webhook channel of its own** — the downstream processor handles event delivery. `VerifyWebhookSignature` always returns `false`; `ParseWebhookAsync` always returns `null`. Configure your webhook endpoint to use Stripe (or whichever downstream) for event notifications.

## Production checklist

Beyond the SDK config, Apple Pay requires:
- An Apple Pay merchant identifier registered at [developer.apple.com](https://developer.apple.com/account/resources/identifiers/list/merchant)
- An Apple Pay payment processing certificate (downstream processor instructions)
- Domain validation file uploaded at `https://<your-domain>/.well-known/apple-developer-merchantid-domain-association`
- An Apple Pay Web Merchant ID for web flows
- The Apple Pay JS framework on the client side

The SDK can't help with any of those — they're Apple Developer Portal tasks.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
