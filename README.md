# Bhengu.Finance.Payments ![Build & Release](https://github.com/bhengubv/Bhengu.Finance.Payments/actions/workflows/release.yml/badge.svg)

A modular .NET 9 SDK for integrating multiple payment providers: PayFast, Google Pay, Apple Pay, and Brics Pay.

---

## ✅ Features

- One-Time Payments
- Refunds
- Subscriptions (Create, Cancel, Status)
- DI-ready services
- Unit & Integration Tests
- Automated versioning, GitHub tagging, and NuGet publishing

---

## 📦 Projects

- `Bhengu.Finance.Payments.Core`: Shared models & interfaces
- `Bhengu.Finance.Payments.PayFast`: PayFast integration
- `Bhengu.Finance.Payments.GooglePay`: Google Pay integration
- `Bhengu.Finance.Payments.ApplePay`: Apple Pay integration
- `Bhengu.Finance.Payments.BricsPay`: BRICS payment processor
- `Bhengu.Finance.Payments.Swagger`: Swagger extensions
- `Bhengu.Finance.Payments.ApiHost`: Minimal API host for testing
- `Bhengu.Finance.Payments.Tests`: Unit tests (xUnit, Moq)
- `Bhengu.Finance.Payments.IntegrationTests`: Sandbox integration tests
- `Bhengu.Finance.Payments.All`: Aggregate package to pull all others at once

---

## 🛠 Setup

```csharp
builder.Services
    .AddPayFastPayments()
    .AddGooglePayments()
    .AddApplePayPayments()
    .AddBricsPayPayments();
