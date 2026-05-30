# Bhengu.Finance.Payments.Stripe

Stripe provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family. Wraps the official `Stripe.net` SDK with the unified `IPaymentGatewayProvider` contract.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Stripe
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Stripe": {
          "SecretKey": "sk_test_...",
          "WebhookSecret": "whsec_..."
        }
      }
    }
  }
}
```

## Wire it up

```csharp
builder.Services.AddStripePayments(builder.Configuration);
```

The provider routes Stripe.net through the injected `HttpClient` (via `StripeClient` + `SystemNetHttpClient`) — so consumer-side `HttpMessageHandler` policies (retries, observability, custom DNS) apply.

## `PaymentMethodToken` semantics

A **Stripe PaymentMethod ID** (`pm_xxx`) from a prior client-side tokenisation (Stripe.js / mobile SDK). Pass it directly:

```csharp
var response = await provider.ProcessPaymentAsync(new PaymentRequest
{
    PaymentMethodToken = "pm_card_visa",
    Amount = 99.99m,
    Currency = "USD",
    Description = "Order #123"
});
```

The provider creates a PaymentIntent with `confirm: true` and `confirmation_method: automatic`, so it settles in one call for non-3DS payments. 3DS challenges fall back to Stripe's standard `requires_action` flow (handled at the client).

## Metadata

All keys in `PaymentRequest.Metadata` are forwarded to Stripe's `metadata` field on the PaymentIntent — visible in the Stripe dashboard and queryable.

## Settlement

**Synchronous** for most cards. `ProcessPaymentAsync` returns `Completed` or `Pending` (3DS / async banks like SEPA) directly. The webhook is still your source of truth for async methods.

## Refunds

Yes — `ProcessRefundAsync` calls `RefundService.Create` with reason mapping (`duplicate` / `fraudulent` / `requested_by_customer`).

## Payouts

Yes — `IPayoutProvider` calls `PayoutService.Create` for Stripe Connect / platform-balance disbursements.

## Webhook

```csharp
app.MapPost("/webhooks/stripe", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Stripe)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["Stripe-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    // evt.EventType is the Stripe event type (e.g. payment_intent.succeeded).
    // evt.GatewayReference is the PaymentIntent ID.
    return Results.Ok();
});
```

The provider recognises these event types: `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.canceled`, `charge.refunded`. Other event types return `null` from `ParseWebhookAsync`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
