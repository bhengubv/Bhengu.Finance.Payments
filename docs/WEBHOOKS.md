# Webhook handling

Every payment provider posts asynchronous notifications (webhooks / IPNs / callbacks) to a URL you expose. The SDK gives you two primitives:

```csharp
bool VerifyWebhookSignature(string payload, string signature);
Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default);
```

## Standard endpoint pattern

```csharp
app.MapPost("/webhooks/{providerName}", async (
    string providerName,
    HttpContext ctx,
    IEnumerable<IPaymentGatewayProvider> providers,
    ILogger<Program> logger,
    IPaymentEventHandler handler) =>
{
    var provider = providers.FirstOrDefault(p => p.ProviderName == providerName);
    if (provider is null) return Results.NotFound();

    // 1. Read the raw body ONCE — providers sign the bytes you received.
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    // 2. Extract the signature header (varies per provider — see table below).
    var signature = ExtractSignature(ctx.Request.Headers, providerName);

    // 3. Verify before acting.
    if (!provider.VerifyWebhookSignature(body, signature))
    {
        logger.LogWarning("Webhook signature failed for {Provider}", providerName);
        return Results.Unauthorized();
    }

    // 4. Parse to a normalised event.
    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null)
    {
        // Recognised the request but not a transaction event — ack so the provider doesn't retry.
        return Results.Ok();
    }

    // 5. Hand to your domain handler (idempotent — webhooks retry).
    await handler.HandleAsync(providerName, evt);
    return Results.Ok();
});
```

## Signature header per provider

| Provider | Header / source | Algorithm |
|---|---|---|
| PayFast | `signature` form field (in body) | MD5 |
| Stripe | `Stripe-Signature` | HMAC-SHA256 timestamped |
| Yoco | `x-yoco-signature` | HMAC-SHA256 base64 |
| Paystack | `x-paystack-signature` | HMAC-SHA512 hex |
| Ozow | `Hash` form field | SHA-512 hex of body+key |
| PayJustNow | `x-payjustnow-signature` | HMAC-SHA256 hex |
| Flutterwave | `verif-hash` | raw secret compare |
| MercadoPago | `x-signature` | HMAC-SHA256 manifest |
| Razorpay | `X-Razorpay-Signature` | HMAC-SHA256 hex |
| Hubtel | `Signature` | HMAC-SHA256 |
| Cellulant | `x-tingg-signature` | HMAC-SHA256 |
| Chipper Cash | `X-Chipper-Signature` | HMAC-SHA256 |
| Airtel Money | body `signature` field | HMAC-SHA256 |
| WeChat Pay | `Wechatpay-Signature` + `Wechatpay-Timestamp` + `Wechatpay-Nonce` + `Wechatpay-Serial` | RSA-SHA256 over canonical |
| Alipay | `signature` header | RSA-SHA256 |
| UnionPay | body `signature` field | RSA-SHA256 over sorted params |
| Apple Pay | n/a — uses downstream processor's webhook | n/a |
| Google Pay | n/a — uses downstream processor's webhook | n/a |
| M-Pesa | n/a — no signature; URL-token + IP allowlist | n/a |
| MTN MoMo | n/a — no signature; rely on callback URL secrecy | n/a |
| Orange Money | `notif_token` in payload — round-trip verify via status API | n/a |
| DPO | n/a — round-trip verify via `verifyToken` call | n/a |

## Idempotency

Providers retry webhooks on non-2xx responses for HOURS. Your handler MUST be idempotent on the `WebhookEvent.GatewayReference`:

```csharp
public async Task HandleAsync(string providerName, WebhookEvent evt)
{
    // INSERT ... ON CONFLICT (gateway_reference, status) DO NOTHING
    var inserted = await _db.RecordWebhookAsync(providerName, evt.GatewayReference, evt.Status);
    if (!inserted) return; // already processed — ack the dupe

    // Real processing here — update transaction, deliver order, notify user, etc.
}
```

## "I didn't get the webhook" — reconciliation

Webhooks fail. The SDK doesn't guarantee delivery; providers don't either. Run a reconciliation job every few minutes:

1. For every `payment_transactions` row with `status='pending'` older than X minutes
2. Call `provider.QueryTransactionAsync(reference)` *(only some providers expose this on the concrete type — PayFast does, Stripe via PaymentIntentService, M-Pesa via stkpushquery)*
3. If the provider says completed/failed and your DB says pending, update.

This catches the case where the webhook was lost in flight.

## Common mistakes

1. **Reading the body twice.** `HttpContext.Request.Body` is a Stream — once read, it's gone. Read into a string ONCE and pass to both `VerifyWebhookSignature` and `ParseWebhookAsync`.
2. **Trimming whitespace before verification.** Providers sign the EXACT bytes they sent. Don't `Trim()`, don't deserialise-then-reserialise.
3. **Verifying after parsing.** Verify FIRST. A signature that doesn't match a parsed event is still a forgery.
4. **Returning non-2xx for unknown event types.** That triggers a retry storm. Return 200 with a noop instead.
5. **Trusting the redirect-back URL.** Browsers can edit query strings; webhooks come from the provider's server. Use webhooks as source of truth.
