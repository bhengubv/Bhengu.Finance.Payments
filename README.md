# Bhengu.Finance.Payments

[![Build & Release](https://github.com/bhengubv/Bhengu.Finance.Payments/actions/workflows/release.yml/badge.svg)](https://github.com/bhengubv/Bhengu.Finance.Payments/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/Bhengu.Finance.Payments.Core.svg)](https://www.nuget.org/packages/Bhengu.Finance.Payments.Core/)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

A modular .NET 10 SDK that puts **46 payment providers behind one typed contract** — South African card processors, African mobile-money rails, Pan-African aggregators, and the full BRICS stack.

Built for the unbanked as much as the banked: M-Pesa, MTN MoMo, Airtel Money, Orange Money, Wave, EcoCash sit alongside Stripe, PayFast, Yoco, Apple Pay, Google Pay, Alipay, WeChat Pay.

> ⚠️ **Honest status:** every provider is **implemented + unit-tested from public API docs, but not yet verified against a live gateway** (all `DocsOnly`), and **four are known-broken pending a rewrite** (DPO, Paytm, WeChat Pay, Alipay). Read **[Implementation status & TODO](#implementation-status--todo)** before you build on this.

## Install (one provider)

```sh
dotnet add package Bhengu.Finance.Payments.PayFast
```

**Don't install `Bhengu.Finance.Payments.All` in production unless you really need every one of the 46 providers** — it pulls every dependency including ones you'll never call.

## Quickstart — single provider

```csharp
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayFast.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPayFastPayments(builder.Configuration);

var app = builder.Build();
app.MapPost("/charge", async (
    [FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider provider) =>
{
    var response = await provider.ProcessPaymentAsync(new PaymentRequest
    {
        PaymentMethodToken = "f4c8e1d2-1234-5678-9abc-def012345678",
        Amount = 99.99m,
        Currency = "ZAR",
        Description = "Demo charge"
    });
    return Results.Ok(response);
});

app.Run();
```

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "PayFast": {
          "MerchantId": "...",
          "MerchantKey": "...",
          "Passphrase": "...",
          "UseSandbox": true
        }
      }
    }
  }
}
```

## Multiple providers — the real shape

Inject a bare `IPaymentGatewayProvider` and DI hands you "whichever was registered last" — a footgun. **Always use keyed services or `IEnumerable<IPaymentGatewayProvider>`.**

```csharp
builder.Services.AddStripePayments(builder.Configuration);
builder.Services.AddPayFastPayments(builder.Configuration);
builder.Services.AddMPesaPayments(builder.Configuration);

// Resolve by name at the endpoint — typed const, no typo risk.
app.MapPost("/charge/{provider}", async (
    string provider,
    [FromBody] PaymentRequest request,
    IEnumerable<IPaymentGatewayProvider> providers) =>
{
    var gw = providers.FirstOrDefault(p => p.ProviderName == provider);
    if (gw is null) return Results.NotFound($"Unknown provider: {provider}");

    // Check capabilities before calling — no need to read provider source.
    if (!gw.Capabilities.HasFlag(ProviderCapabilities.Charge))
        return Results.BadRequest($"{provider} doesn't support charge.");

    var response = await gw.ProcessPaymentAsync(request);
    return response.RedirectUrl is not null
        ? Results.Redirect(response.RedirectUrl)
        : Results.Ok(response);
});
```

Or for a single provider, `[FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider gw`.

## Apple Pay / Google Pay

Both tokenise only — they forward to a downstream processor. Register the downstream first, then Apple/Google Pay validates at startup.

```csharp
builder.Services.AddStripePayments(builder.Configuration);  // downstream first
builder.Services.AddApplePayPayments(builder.Configuration); // validates at startup
```

`appsettings.json`:
```json
"ApplePay": { "MerchantId": "merchant.com.your-app", "DownstreamProcessor": "stripe" }
```

If you misconfigure (typo the processor name, forget to register Stripe), the app **crashes at startup with a clear `ProviderConfigurationException`** — not on the first inbound Apple Pay request.

## Webhooks

```csharp
app.MapPost("/webhooks/payfast", async (
    HttpContext ctx,
    [FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider provider) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    var signature = ctx.Request.Headers["X-Signature"].ToString();

    if (!provider.VerifyWebhookSignature(body, signature))
        return Results.Unauthorized();

    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null) return Results.Ok(); // unrecognised event; ack to avoid retries

    // Use evt.GatewayReference + evt.Status to update your DB.
    return Results.Ok();
});
```

## Provider catalogue (46)

### South Africa (8)
PayFast · Yoco · Ozow · PayJustNow · PayShap · Mukuru · TymeBank · Stitch

### Mobile money for the unbanked (6)
M-Pesa (KE/TZ/MZ/DRC/EG/ET/GH) · MTN MoMo (17 countries) · Airtel Money (14 countries) · Orange Money (18 Francophone countries) · Wave (SN/CI/ML/UG) · EcoCash (ZW)

### Pan-African aggregators (4)
Flutterwave (30+ countries) · Cellulant Tingg (35 countries) · DPO Group · Onafriq (500M wallets)

### Nigeria (5) / Ghana (3) / Kenya (3) / Egypt (3) / Morocco (1)
Paystack · Interswitch · OPay · Moniepoint · Remita | Hubtel · ExpressPay · Slydepay | Pesapal · IPay · JamboPay | Fawry · Paymob · Kashier | CMI

### Pan-African transfers (1)
Chipper Cash

### BRICS — Brazil (2) / India (3) / China (3)
Mercado Pago · PagSeguro | Razorpay · PayU India · Paytm | Alipay · WeChat Pay · UnionPay

### Cross-border BRICS rail (1)
BRICS Pay (ZAR/BRL/RUB/INR/CNY)

### Global card networks (3)
Stripe · Apple Pay · Google Pay

## Feature matrix

> ✅ here means **implemented + unit-tested against the public API docs** — **not** confirmed against a live gateway (every provider is `DocsOnly`; see [Implementation status & TODO](#implementation-status--todo)). Name markers: 🔴 broken / rebuild-pending · 🚪 gated scaffold (every call throws) · 📦 archived · 🅿️ parked / not built · ↔️ rerouted.

Use `provider.Capabilities.HasFlag(ProviderCapabilities.X)` to check at runtime.

| Provider | Charge | Refund | Payout | Webhook | Cards | Mobile money | Bank transfer | Cross-border |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| **South Africa** ||||||||
| PayFast | ✅ | ⚠ manual | — | ✅ | ✅ | — | — | — |
| Yoco | ✅ | ✅ | — | ✅ | ✅ | — | — | — |
| Ozow | ✅ | ✅ | — | ✅ | — | — | ✅ | — |
| PayShap 🅿️ | — | — | — | — | — | — | — | — |
| Stitch | ✅ | ✅ | ✅ | ✅ | — | — | ✅ | — |
| TymeBank 📦 | — | — | — | — | — | — | — | — |
| Mukuru ↔️ | ✅ | ✅ | — | ✅ | — | — | — | — |
| **Mobile money** ||||||||
| M-Pesa | ✅ | ✅ | ✅ | ✅ | — | ✅ | — | — |
| MTN MoMo | ✅ | — | ✅ | ✅ | — | ✅ | — | — |
| Airtel Money | ✅ | ✅ | ✅ | ✅ | — | ✅ | — | — |
| Orange Money | ✅ | — | — | ✅ | — | ✅ | — | — |
| Wave | ✅ | ✅ | ✅ | ✅ | — | ✅ | — | — |
| EcoCash | ✅ | ✅ | — | — | — | ✅ | — | — |
| **Aggregators** ||||||||
| Flutterwave | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Cellulant | ✅ | ✅ | ✅ | ✅ | — | ✅ | — | ✅ |
| DPO 🔴 | ✅ | ✅ | — | ✅ | ✅ | — | — | ✅ |
| Onafriq | ✅ | — | ✅ | ✅ | — | ✅ | — | ✅ |
| Chipper Cash 🚪 | — | — | — | — | — | — | — | — |
| **Nigeria / Ghana / Kenya / Egypt / Morocco** ||||||||
| Paystack | ✅ | ✅ | ✅ | ✅ | ✅ | — | ✅ | — |
| Interswitch | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | — |
| OPay | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — | — |
| Moniepoint | ✅ | ✅ | ✅ | ✅ | ✅ | — | ✅ | — |
| Remita | ✅ | — | — | ✅ | — | — | ✅ | — |
| Hubtel | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | — | — |
| ExpressPay | ✅ | — | — | ✅ | ✅ | ✅ | — | — |
| Slydepay | ✅ | — | — | ✅ | ✅ | ✅ | — | — |
| Pesapal | ✅ | ✅ | — | ✅ | ✅ | ✅ | — | — |
| IPay | ✅ | — | — | ✅ | ✅ | ✅ | — | — |
| JamboPay 🚪 | — | — | — | — | — | — | — | — |
| Fawry | ✅ | ✅ | — | ✅ | ✅ | — | — | — |
| Paymob | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | — |
| Kashier | ✅ | ✅ | — | ✅ | ✅ | — | — | — |
| CMI | ✅ | ✅ | — | ✅ | ✅ | — | — | — |
| **BRICS** ||||||||
| BRICS Pay | ✅ | ✅ | ✅ | ✅ | — | — | — | ✅ |
| Mercado Pago | ✅ | ✅ | — | ✅ | ✅ | — | ✅ | — |
| PagSeguro | ✅ | ✅ | ✅ | ✅ | ✅ | — | ✅ | — |
| Razorpay | ✅ | ✅ | ✅ | ✅ | ✅ | — | ✅ | — |
| PayU India | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | — |
| Paytm 🔴 | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | — |
| Alipay 🔴 | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | ✅ |
| WeChat Pay 🔴 | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | — |
| UnionPay | ✅ | ✅ | — | ✅ | ✅ | — | — | — |
| **Global** ||||||||
| Stripe | ✅ | ✅ | ✅ | ✅ | ✅ | — | — | — |
| Apple Pay¹ | ✅ | ✅ | — | — | ✅ | — | — | — |
| Google Pay¹ | ✅ | ✅ | — | — | ✅ | — | — | — |

¹ Apple Pay / Google Pay tokenise only. Settlement is via the configured downstream processor (default: Stripe).

## Implementation status & TODO

**Read this before building on the catalogue.** Every provider is implemented from its **public API documentation and covered by unit tests**, but **none has been verified against a live sandbox or production gateway yet** — all carry `[ProviderVerificationStatus(DocsOnly)]`. In the matrix above, ✅ means *"implemented + unit-tested,"* **not** *"confirmed against the real gateway."* Live verification is owned separately and is **not done for any provider.**

### 🔴 Known-broken — do NOT use (rebuild pending)
The wire format is wrong; these will fail against the real gateway.
- [ ] **DPO Group** — code speaks JSON; the real DPO API is **XML** (`<API3G>` over `secure.3gdirectpay.com/API/v6/`). Full XML rewrite needed.
- [ ] **Paytm** — signs with HMAC-SHA256; Paytm requires its **AES-128-CBC + SHA-256 `PaytmChecksum`**.
- [ ] **WeChat Pay** — targets mainland v3 endpoints; cross-border needs the **global** scheme (`apihk.mch.weixin.qq.com/v3/global/…`).
- [ ] **Alipay** — single hardcoded global host; needs **region partitioning + RSA2** signing.

### 🅿️ Parked / not built
- [ ] **PayShap** — to be built as a redirect over **Ozow** (now unblocked — Ozow's redirect flow is fixed).

### 📦 Archived
- **TymeBank** — removed from the build; no usable public merchant API. (Still shown in the matrix for reference; it ships nothing.)

### ↔️ Rerouted
- **Mukuru** — now a thin redirect over **PayFast** (charge / refund / webhook only). Its old payout / mobile-money / cross-border capabilities are gone.

### 🚪 Gated scaffolds — registered but every call throws `not_available`
These compile and register for DI parity but **cannot transact** — access needs merchant credentials the providers don't issue self-serve.
- [ ] **Chipper Cash** — API is behind a Typeform application; no public self-serve.
- [ ] **JamboPay** — developer docs offline/dead; spec unobtainable.

### ✂️ Fiction removed (capabilities deleted — not broken, just honest)
- **MercadoPago** — payout removed (no public MercadoPago disbursement endpoint).
- **Kashier** — payout removed (no public Kashier payout endpoint).
- **PayJustNow** — invented subscription + mandate providers deleted (not in the public API).
- **Remita** — payout & refund now throw `not_supported` (real disbursement is the separate RITS product).
- **iPay** — refund throws `not_supported` (the v3 API has none).

### 🟡 Implemented against real docs, with `// UNVERIFIED:` parts to confirm in sandbox
Core flow is real and tested; the listed pieces aren't in the public docs.
- **Hubtel** — server-side transaction-status path; settlement statement paths.
- **OPay** — payout / tokenisation / settlement endpoints; webhook needs HMAC-**SHA3-512** runtime support (fails closed if absent).
- **Ozow** — refund endpoint (Ozow documents refunds via the portal, not REST).
- **Cellulant (Tingg)** — payout body & credentials; marketplace sub-account onboarding; settlement; webhooks are **unsigned** (authenticate via status re-query).
- **Kashier** — 3-D Secure field names; tokenisation cipher; hash key (Secret vs API).
- **Remita** — webhook signature scheme.
- **EcoCash** — C2B charge success-response body (no webhook — poll status).
- **iPay** — web-flow callback verification is a server-to-server re-query, not an HMAC.

### 🟢 Implemented against the real published API + unit-tested (DocsOnly — pending live verification)
PayFast · Yoco · Stitch · M-Pesa · MTN MoMo · Airtel Money · Orange Money · Wave · Onafriq · Flutterwave · Paystack · Interswitch · Moniepoint · Fawry · Paymob · CMI · PayJustNow · MercadoPago · PagSeguro · Razorpay · PayU India · UnionPay · Stripe · Apple Pay · Google Pay · BRICS Pay · Pesapal · ExpressPay · Slydepay
*(the 🟡 group's core flows are implemented + tested too — they differ only in the unverified parts listed above.)*

> **For 2.0.0:** the four 🔴 rebuilds + PayShap are the release blockers. The 🟡 items are sandbox-confirmation tasks, not blockers. **Nothing is live-verified yet** — that pass is owned separately and gates "production-ready," not "release."

## Design

Every provider implements:

```csharp
public interface IPaymentGatewayProvider
{
    string ProviderName { get; }
    ProviderCapabilities Capabilities { get; }

    Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default);
    Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default);
    bool VerifyWebhookSignature(string payload, string signature);
    Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default);
}
```

Providers with payouts also implement `IPayoutProvider`. Failures throw typed exceptions — `PaymentDeclinedException`, `WebhookSignatureException`, `ProviderRateLimitException`, `ProviderUnavailableException`, `ProviderConfigurationException` — all derived from `BhenguPaymentException` so consumers can catch broad or narrow.

Configuration is bound from `Bhengu:Finance:Payments:<Provider>` in `IConfiguration`. `AddXxxPayments()` validates required options at registration time and registers a hosted service that materialises every provider at app startup — so misconfiguration crashes the app at startup, not at first request.

## House style

The `Bhengu.*` package family follows a shared house style — naming, interface shape, DI/config conventions, semver. See [HOUSE_STYLE.md](../Bhengu.Family/HOUSE_STYLE.md).

## License

Apache 2.0. See [LICENSE](LICENSE).

© 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
