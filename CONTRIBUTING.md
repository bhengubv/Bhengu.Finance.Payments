# Contributing

Thanks for considering a contribution. The `Bhengu.Finance.Payments` SDK lives or dies by being **easy to adopt for any .NET developer**, so we hold consistency and ergonomics above almost everything else.

## House style

We follow a shared spec across the `Bhengu.*` package family — naming, interface shape, DI / config conventions, semver. Read it first: [`../Bhengu.Family/HOUSE_STYLE.md`](../Bhengu.Family/HOUSE_STYLE.md).

## Build + test locally

```sh
git clone https://github.com/bhengubv/Bhengu.Finance.Payments
cd Bhengu.Finance.Payments
dotnet test Bhengu.Finance.Payments.Tests/Bhengu.Finance.Payments.Tests.csproj
```

Requires the .NET 10 SDK. The packages multi-target `net8.0` and `net10.0`; tests run on `net10.0`.

## Adding a new provider

A new payment-provider SDK is 4 files in a new folder + a test file. The pattern is:

```
Bhengu.Finance.Payments.<Provider>/
  Bhengu.Finance.Payments.<Provider>.csproj
  Configuration/<Provider>Options.cs       — IConfiguration-bound options
  Providers/<Provider>PaymentProvider.cs   — implements IPaymentGatewayProvider (+ optional IPayoutProvider)
  Extensions/ServiceCollectionExtensions.cs — Add<Provider>Payments(IConfiguration)
  README.md                                — per-package docs (see existing PayFast/Stripe/MPesa/ApplePay)

Bhengu.Finance.Payments.Tests/<Provider>/
  <Provider>PaymentProviderTests.cs        — ~12–16 unit tests covering the surface
```

Use the **Yoco** package as the simplest reference for cards + the **MPesa** package for mobile money + the **ApplePay** package for tokenise-and-forward.

### Required checks before opening a PR

1. **Capabilities declared.** Your provider sets `Capabilities` with the flags that match what you implemented. Don't claim `Refund` if `ProcessRefundAsync` throws "not supported".
2. **PaymentMethodToken documented.** Your provider's class XML doc explains what shape the consumer passes — a phone number, a Stripe `pm_xxx` ID, a Base64 token, etc.
3. **Metadata keys documented.** If your provider reads any keys from `PaymentRequest.Metadata` (e.g. `email` for Paystack, `payshap.payer.account` for PayShap), list them in the class XML doc.
4. **Typed exceptions only.** No raw `throw new Exception(...)`. Use `PaymentDeclinedException`, `ProviderRateLimitException`, `ProviderUnavailableException`, `ProviderConfigurationException`, or `BhenguPaymentException` as the base.
5. **Webhook signature consistent.** `VerifyWebhookSignature` returns `false` (with a log warning) on missing config — does NOT throw. Returns `false` on tampered signatures.
6. **DI extension calls `AddBhenguPaymentStartupValidation`.** So misconfig surfaces at startup, not first request.
7. **Keyed-service registration.** `services.AddKeyedTransient<IPaymentGatewayProvider>(ProviderNames.X, ...)` in the extension. Add a `ProviderNames.X` const.
8. **Test coverage.** New provider needs ~12–16 tests covering: ctor validation, success-path response parsing, refund, payout (if applicable), 429/4xx/5xx/HttpRequestException mapping, webhook signature valid+tampered+missing, ParseWebhookAsync good+invalid+unknown-type.
9. **README.md in the package folder.** The package README ships inside the nupkg (we have `<PackageReadmeFile>` wired). Cover: install, configuration JSON, `PaymentMethodToken` semantics, Metadata keys, settlement (sync/async), refund support, payout support, webhook signature scheme.

## Commit / PR conventions

- Conventional-commits style: `feat(provider): add Foo`, `fix(stripe): correct webhook header parse`, `docs: add resilience guide`.
- One provider per PR ideally — keeps the diff reviewable.
- PRs run the test suite on push. Green CI is required before merge.

## Tests

We use xUnit + Moq. The shared `StubHttpMessageHandler` test helper at `Bhengu.Finance.Payments.Tests/TestHelpers/StubHttpMessageHandler.cs` mocks `HttpClient` responses — use it.

Don't add integration tests against real provider sandboxes in the main test project; those live in `Bhengu.Finance.Payments.IntegrationTests` and only run when credentials are present.

## Security

If you find a security issue, do NOT open a public issue. Email security@thegeek.co.za — see [SECURITY.md](SECURITY.md).

## License

By contributing you agree that your contribution is Apache-2.0 licensed.
