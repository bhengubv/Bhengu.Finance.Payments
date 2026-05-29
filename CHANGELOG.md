# Changelog

All notable changes to `Bhengu.Finance.Payments` packages are documented here.
This project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0-preview.1] — 2026-05-29

### Breaking — full SDK redesign per the `Bhengu.*` family house style

All packages in this release adopt the typed `IPaymentGatewayProvider` interface and the typed exception hierarchy. Pre-1.0 stub interfaces (`IPaymentService` / `ISubscriptionService` returning `Task<string>`) are gone. Callers of pre-`1.0.0-preview.1` versions must migrate.

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
