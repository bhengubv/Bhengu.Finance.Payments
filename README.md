# Bhengu.Finance.Payments

A modular .NET 9 SDK for integrating multiple payment providers: PayFast, Google Pay, Apple Pay, and Brics Pay.

## ✅ Features

- One-Time Payments
- Refunds
- Subscriptions (Create, Cancel, Status)
- DI-ready services
- Unit & Integration Tests

## 📦 Projects

- Core: Shared models & interfaces
- Vendor Packages: PayFast, Google, ApplePay, BricsPay
- Swagger: Extensions for documentation
- ApiHost: Minimal API host for testing
- Tests: Unit tests (xUnit, Moq)
- IntegrationTests: Real HTTPClient sandbox tests

## 🛠 Setup

```csharp
builder.Services
    .AddPayFastPayments()
    .AddGooglePayments()
    .AddApplePayPayments()
    .AddBricsPayPayments();
```

## 🧾 Configuration

See `appsettings.Development.json` for example setup per vendor.

## 🔍 Usage

```csharp
var result = await payFastService.InitiateAsync(new PaymentRequest
{
    Amount = 99.99m,
    ItemName = "Gold Plan"
});
```

## 🧪 Tests

Run unit tests:
```bash
dotnet test Bhengu.Finance.Payments.Tests
```

Run integration tests (with sandbox credentials):
```bash
dotnet test Bhengu.Finance.Payments.IntegrationTests
```