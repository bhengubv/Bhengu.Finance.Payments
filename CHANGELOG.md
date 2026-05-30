# Changelog

All notable changes to `Bhengu.Finance.Payments` packages are documented here.
This project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0-preview.2] — 2026-05-30

### Breaking — ubiquity pass + 36 new providers

A fresh-eyes DX audit surfaced friction points blocking adoption. This release fixes them and adds the rest of the African + BRICS landscape we'd been missing.

### Added — 36 new providers (now 46 total)

**African mobile money for the unbanked:** M-Pesa (KE/TZ/MZ/DRC/EG/ET/GH), MTN MoMo (17 countries), Airtel Money (14 countries), Orange Money (18 Francophone countries), Wave (SN/CI/ML/UG), EcoCash (ZW).

**Pan-African aggregators:** Flutterwave (30+ countries), Cellulant (35 countries), DPO Group, Onafriq (500M wallets), Chipper Cash.

**Country gateways:** Interswitch · OPay · Moniepoint · Remita (Nigeria) · Hubtel · ExpressPay · Slydepay (Ghana) · Pesapal · IPay · JamboPay (Kenya) · Fawry · Paymob · Kashier (Egypt) · CMI (Morocco) · Mukuru · TymeBank · Stitch (South Africa).

**BRICS-completing:** Mercado Pago, PagSeguro (Brazil) · Razorpay, PayU India, Paytm (India) · Alipay, WeChat Pay, UnionPay (China).

### Added — Core

- `ProviderNames` — typed constants for all 46 providers. Use `ProviderNames.PayFast` instead of `"payfast"` to eliminate typo risk.
- `ProviderCapabilities` `[Flags]` — query a provider's surface at runtime: `provider.Capabilities.HasFlag(ProviderCapabilities.Refund)`. Flags include Charge, Refund, Payout, Webhook, SyncSettlement, RedirectFlow, Tokeniser, CrossBorder, MobileMoney, Cards, BankTransfer.
- `IPaymentGatewayProvider.Capabilities` — every provider now declares what it supports.
- `PaymentResponse.RedirectUrl` — first-class property for redirect URLs (previously overloaded into `Message`).
- `IRequiresPostConstructionValidation` — opt-in interface for providers needing cross-provider validation (Apple Pay / Google Pay checking their downstream is registered).
- `BhenguPaymentStartupValidator` (`IHostedService`) — registered by every `AddXxxPayments()` extension; eagerly materialises every provider at app startup so misconfiguration crashes the app at startup, not at first request.

### Changed — DX / API ergonomics

- **Every `AddXxxPayments()` extension now also registers a keyed service.** Consumers can inject by name without LINQ: `[FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider provider`.
- **`PaymentResponse.Message`** is no longer overloaded with URLs. URLs go to `RedirectUrl`; `Message` is human-readable status text only.
- **`VerifyWebhookSignature` normalised** — all providers return `false` on missing-secret configuration with a log warning. Previously some threw, some returned false, some silently failed; now uniformly false-on-missing.
- **Apple Pay + Google Pay validate their downstream processor at app startup**, not at first request. Misconfiguration is a startup crash with a clear `ProviderConfigurationException` — no more sev-1 pager at midnight when the first Apple Pay charge hits an unregistered downstream.

### Documentation

- README rewritten: multi-provider Quickstart with keyed-services + a 46-row feature matrix.
- Explicit warning against `Bhengu.Finance.Payments.All` in production (pulls 46 transitive deps).
- Per-package READMEs added for PayFast, Stripe, M-Pesa, Apple Pay — each documents what goes in `PaymentMethodToken`, which `Metadata` keys are read, sync-vs-async settlement, refund support, and webhook semantics.

### Test coverage

706 unit tests passing across all 46 providers — constructor validation, success path with response parsing, refund, payout (where applicable), HTTP failure mapping (429/4xx/5xx/network), webhook signature valid+tampered+missing, and `ParseWebhookAsync` good+invalid+unknown-type.

### Migration from `2.0.0-preview.1`

- **Implementations of `IPaymentGatewayProvider`** must add a `Capabilities` property (declare which flags apply). Consumers don't need to do anything.
- **Consumers reading the URL from `PaymentResponse.Message`** should read `PaymentResponse.RedirectUrl` instead.
- **Consumers using bare `IPaymentGatewayProvider`** with multiple providers registered should switch to `[FromKeyedServices(ProviderNames.X)]` or filter `IEnumerable<IPaymentGatewayProvider>` by `ProviderName`. (The bare injection still works but silently returns "whichever was registered last" — DI default behaviour.)



## [2.0.0-preview.1] — 2026-05-29

### Breaking — full SDK redesign per the `Bhengu.*` family house style

All packages in this release adopt the typed `IPaymentGatewayProvider` interface and the typed exception hierarchy. Pre-2.0 stub interfaces (`IPaymentService` / `ISubscriptionService` returning `Task<string>`) are gone. Callers of `1.0.32` and earlier must migrate.

**Why 2.0.0 and not 1.1.0?** Per the `Bhengu.*` house style semver rules, *anything that breaks a caller* is a major version. The interface, return types, and exception model all changed — every consumer needs to adapt. The previous `1.x` line stays available on NuGet for callers who can't migrate yet.

**Why `-preview.1`?** The redesign is opinionated and the surface may still tighten before `2.0.0` final. Pre-release tag signals "API still tentative — feedback welcome before lock." See HOUSE_STYLE.md §Semver.

### Added

- **Core (`Bhengu.Finance.Payments.Core`)** — new typed interface `IPaymentGatewayProvider` with `ProcessPaymentAsync`, `ProcessRefundAsync`, `VerifyWebhookSignature`, `ParseWebhookAsync` returning typed records. Optional `IPayoutProvider` for payout-capable providers. Typed exception hierarchy: `BhenguPaymentException` (base) + `PaymentDeclinedException`, `WebhookSignatureException`, `ProviderRateLimitException`, `ProviderUnavailableException`, `ProviderConfigurationException`. Normalised `PaymentStatus` enum.
- **PayFast** — full server-to-server provider (tokenised ad-hoc) plus browser-redirect `PayFastFormBuilder` (once-off, subscription, tokenisation flows). Provider-specific extras `FetchTokenAsync`, `CancelTokenAsync`, `QueryTransactionAsync` accessible on the concrete type.
- **BRICS Pay** — implements both `IPaymentGatewayProvider` and `IPayoutProvider`. Bundled `ICurrencyExchangeService` for ZAR/BRL/RUB/INR/CNY conversion with live rates from frankfurter.app and a static baseline fallback.
- **Stripe, Yoco, Paystack, Ozow, PayJustNow** — new SDK projects per provider, conformed to the new interface.
- **Apple Pay, Google Pay** — scaffolds. Throw typed `BhenguPaymentException` on call until merchant onboarding / downstream processor selection is completed.
- **PayShap** — adapter to `IPaymentGatewayProvider` for cross-provider tooling; the rich `IPayShapService` remains the recommended consumption path for real PayShap workflows.
- DI convention: every package ships an `AddXxxPayments(IConfiguration)` extension that reads `Bhengu:Finance:Payments:<Provider>` and validates required options at registration time.

### Changed

- Target framework bumped to **.NET 10** (was net9.0).
- License switched to **Apache 2.0** (was MIT). Explicit patent grant matters as the family encodes novel work.
- Configuration keys standardised to `Bhengu:<Domain>:<Subject>:<Provider>`.

### Fixed

- `Bhengu.Finance.Payments.PayShap.csproj` had a duplicate `<PackageId>` line that mis-identified the package as PayFast. Past versions shipped with the correct ID due to MSBuild last-wins precedence, but the file is now clean.
- `Bhengu.Finance.Payments.Tests` and `Bhengu.Finance.Payments.IntegrationTests` projects were missing `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio`, so `dotnet test` previously found zero tests. Added.
- `IntegrationTests.csproj` had `IsPackable=true` (would have packed a test project as a NuGet package). Now `false`.

### Removed

- Pre-redesign stub services: `PayFastService`, `BricsPayService`, `ApplePayService`, `GoogleService` and their `IXxx` interfaces (returned hardcoded strings, conflicted with the new typed interface).
- Pre-redesign Core interfaces: `IPaymentService`, `ISubscriptionService` (will be redesigned and reintroduced when a real subscription-capable provider lands).
- `ProcessRequest` and `SubscriptionRequest` DTOs (only referenced by the removed interfaces).

### Migration from `1.0.32` and earlier

There are no existing real consumers (prior versions shipped stub providers). If you wrote test code against the stubs:

- Replace `IPaymentService.InitiateAsync(PaymentRequest)` with `IPaymentGatewayProvider.ProcessPaymentAsync(PaymentRequest, CancellationToken)` and adapt to the new `PaymentRequest` / `PaymentResponse` records (`PaymentMethodToken`, `Currency`, `Description`, `Metadata` are now required / typed).
- Wrap calls in `try { ... } catch (BhenguPaymentException ex) { ... }` — failures throw typed exceptions instead of returning strings.
