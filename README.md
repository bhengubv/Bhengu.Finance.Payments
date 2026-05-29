# Bhengu.Finance.Payments

[![Build & Release](https://github.com/bhengubv/Bhengu.Finance.Payments/actions/workflows/release.yml/badge.svg)](https://github.com/bhengubv/Bhengu.Finance.Payments/actions/workflows/release.yml)
[![NuGet](https://img.shields.io/nuget/v/Bhengu.Finance.Payments.Core.svg)](https://www.nuget.org/packages/Bhengu.Finance.Payments.Core/)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

A modular .NET 10 SDK for integrating multiple payment providers behind a single typed contract.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.PayFast
```

Or pull every provider via the meta-package:

```sh
dotnet add package Bhengu.Finance.Payments.All
```

## Quickstart

```csharp
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayFast.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPayFastPayments(builder.Configuration);

var app = builder.Build();
app.MapPost("/charge", async (IPaymentGatewayProvider provider) =>
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

Configure `appsettings.json`:

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

## Providers

| Provider | Package | Status | Payouts | Refund API |
|---|---|---|---|---|
| PayFast (ZA) | `Bhengu.Finance.Payments.PayFast` | Production | — | Manual only |
| BRICS Pay | `Bhengu.Finance.Payments.BricsPay` | Production | ✅ | ✅ |
| Stripe | `Bhengu.Finance.Payments.Stripe` | Production | ✅ | ✅ |
| Yoco | `Bhengu.Finance.Payments.Yoco` | Production | ✅ | ✅ |
| Paystack | `Bhengu.Finance.Payments.Paystack` | Production | — | ✅ |
| Ozow | `Bhengu.Finance.Payments.Ozow` | Production | — | ✅ |
| PayJustNow | `Bhengu.Finance.Payments.PayJustNow` | Production | — | ✅ |
| PayShap | `Bhengu.Finance.Payments.PayShap` | Production (via `IPayShapService`) | n/a | n/a |
| Apple Pay | `Bhengu.Finance.Payments.ApplePay` | Scaffold — see package README | — | — |
| Google Pay | `Bhengu.Finance.Payments.GooglePay` | Scaffold — see package README | — | — |

## Design

Every provider implements `IPaymentGatewayProvider` (see `Bhengu.Finance.Payments.Core`):

```csharp
public interface IPaymentGatewayProvider
{
    string ProviderName { get; }
    Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default);
    Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default);
    bool VerifyWebhookSignature(string payload, string signature);
    Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default);
}
```

Providers that support payouts also implement `IPayoutProvider`. The SDK uses typed exceptions for failure (`PaymentDeclinedException`, `WebhookSignatureException`, `ProviderRateLimitException`, `ProviderUnavailableException`, `ProviderConfigurationException`) — all derived from `BhenguPaymentException`.

## House style

The `Bhengu.*` package family follows a shared house style — naming, interface shape, DI/config conventions, semver. See [HOUSE_STYLE.md](../Bhengu.Family/HOUSE_STYLE.md).

## License

Apache 2.0. See [LICENSE](LICENSE).

© 2026 The Other Bhengu (Pty) Ltd t/a The Geek.
