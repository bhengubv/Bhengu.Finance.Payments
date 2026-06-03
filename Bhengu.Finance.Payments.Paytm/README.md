# Bhengu.Finance.Payments.Paytm

Paytm (India) provider for the [Bhengu.Finance.Payments](https://github.com/bhengubv/Bhengu.Finance.Payments) SDK family.

All-in-One Payments via Paytm's `theia` initiate-transaction + hosted checkout, refund, and Paytm Payouts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Paytm
```

## Configuration

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Paytm": {
          "MerchantId": "MID...",
          "MerchantKey": "...",
          "WebsiteName": "DEFAULT",
          "CallbackUrl": "https://yoursite.example/webhooks/paytm",
          "Industry": "Retail",
          "Currency": "INR",
          "UseSandbox": true
        }
      }
    }
  }
}
```

Required: `MerchantId` (MID) and `MerchantKey`. `WebsiteName` is `WEBSTAGING` in sandbox, `DEFAULT` (or custom) in production.

## Wire it up

```csharp
builder.Services.AddPaytmPayments(builder.Configuration);
```

Validates `MerchantId` and `MerchantKey` at registration.

## Checksum simplification

**This SDK uses base64-encoded HMAC-SHA256(payload, MerchantKey) as the "signature"** instead of Paytm's official AES-128-CBC + SHA-256 + random-salt checksum algorithm. This is documented in the provider's source. Production merchants needing strict Paytm-compatible checksum handling should wrap the provider with their own checksum helper — the rest of the SDK contract is unchanged.

## `PaymentMethodToken` semantics

The merchant **`custId`** (Paytm customer identifier). The provider uses it directly as the user `custId` in the initiate body when no `custId` metadata override is supplied.

## Metadata keys

| Key | Required | Format | Example |
| --- | --- | --- | --- |
| `orderId` | Optional | Merchant order id (defaults to `ORDER_<guid>`) | `order-123` |
| `callbackUrl` | Optional | Override `CallbackUrl` option | `https://...` |
| `custId` | Optional | Customer identifier (overrides `PaymentMethodToken`) | `cust-42` |
| `mobile` | Optional | Phone number | `+919876543210` |
| `email` | Optional | E-mail | `buyer@example.com` |
| `firstName` | Optional | Given name | `Rahul` |
| `lastName` | Optional | Family name | `Sharma` |

## `PayoutRequest.DestinationToken` format

The recipient's **Paytm wallet identifier** (`beneficiary`) — the payee's `custId` or VPA, passed verbatim. The SDK currently calls `disburse/v1/order/wallet`.

## Settlement

**Asynchronous.** `ProcessPaymentAsync` calls `theia/api/v1/initiateTransaction`, returns `Pending` plus the `showPaymentPage` URL as `RedirectUrl`. The `txnToken` from the response is appended to `PaymentResponse.Message` (`txnToken=...`) — your client passes it to Paytm's checkout widget. Real outcome arrives via webhook to `CallbackUrl`. Amounts formatted as `"0.00"`.

## Refunds

Yes — `ProcessRefundAsync` calls `refund/apply` with `mid`, `orderId`, optional `txnId`, `refId`, and `refundAmount`. Pass `txnId` by prefixing the `Reason` with `"txnId:<value>;"` if your Paytm config requires it.

## Payouts

**Yes.** `IPayoutProvider.ProcessPayoutAsync` calls `disburse/v1/order/wallet`.

## Webhook

Base64 HMAC-SHA256 of the body using `MerchantKey` (per the checksum simplification above), in a checksum header that the caller is responsible for extracting.

```csharp
app.MapPost("/webhooks/paytm", async (HttpContext ctx,
    [FromKeyedServices(ProviderNames.Paytm)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var checksum = ctx.Request.Headers["x-paytm-checksum"].ToString();

    if (!provider.VerifyWebhookSignature(body, checksum))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    return Results.Ok();
});
```

Status values mapped (Paytm uses both legacy `TXN_SUCCESS`/`TXN_PENDING`/`TXN_FAILURE` and short `S`/`P`/`F`): success/`s` → Completed; pending/`p` → Pending; failure/`f` → Failed; refunded → Refunded.

## Capabilities

`Charge | Refund | Payout | Webhook | RedirectFlow | Cards`.

## License

Apache 2.0. © 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
