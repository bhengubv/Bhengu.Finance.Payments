// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Verifies the <c>RequireVerifiedProviders</c> startup gate — refuses DocsOnly providers
/// unless explicitly allowlisted.
/// </summary>
public class StartupValidatorVerificationTests
{
    [ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly)]
    private sealed class UnverifiedProvider : IPaymentGatewayProvider
    {
        public string ProviderName => "unverified";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Charge;
        public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RefundResponse> ProcessRefundAsync(RefundRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public bool VerifyWebhookSignature(string payload, string signature) => true;
        public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) => Task.FromResult<WebhookEvent?>(null);
    }

    [ProviderVerificationStatus(ProviderVerificationStatus.SandboxVerified)]
    private sealed class VerifiedProvider : IPaymentGatewayProvider
    {
        public string ProviderName => "verified";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Charge;
        public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RefundResponse> ProcessRefundAsync(RefundRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public bool VerifyWebhookSignature(string payload, string signature) => true;
        public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) => Task.FromResult<WebhookEvent?>(null);
    }

    [Fact]
    public async Task DefaultModePassesWithUnverifiedProvider()
    {
        // RequireVerifiedProviders = false (default) → no gating
        var validator = new BhenguPaymentStartupValidator(
            new IPaymentGatewayProvider[] { new UnverifiedProvider() },
            Options.Create(new BhenguPaymentStartupValidationOptions()),
            NullLogger<BhenguPaymentStartupValidator>.Instance);
        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task StrictModeRefusesDocsOnlyProvider()
    {
        var validator = new BhenguPaymentStartupValidator(
            new IPaymentGatewayProvider[] { new UnverifiedProvider() },
            Options.Create(new BhenguPaymentStartupValidationOptions { RequireVerifiedProviders = true }),
            NullLogger<BhenguPaymentStartupValidator>.Instance);

        var ex = await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
            validator.StartAsync(CancellationToken.None));
        Assert.Contains("unverified", ex.Message);
    }

    [Fact]
    public async Task StrictModePassesVerifiedProvider()
    {
        var validator = new BhenguPaymentStartupValidator(
            new IPaymentGatewayProvider[] { new VerifiedProvider() },
            Options.Create(new BhenguPaymentStartupValidationOptions { RequireVerifiedProviders = true }),
            NullLogger<BhenguPaymentStartupValidator>.Instance);
        await validator.StartAsync(CancellationToken.None); // must not throw
    }

    [Fact]
    public async Task StrictModeAllowsExplicitlyAllowlistedDocsOnlyProvider()
    {
        var validator = new BhenguPaymentStartupValidator(
            new IPaymentGatewayProvider[] { new UnverifiedProvider() },
            Options.Create(new BhenguPaymentStartupValidationOptions
            {
                RequireVerifiedProviders = true,
                AllowUnverifiedProviders = { "unverified" },
            }),
            NullLogger<BhenguPaymentStartupValidator>.Instance);
        await validator.StartAsync(CancellationToken.None); // must not throw
    }
}
