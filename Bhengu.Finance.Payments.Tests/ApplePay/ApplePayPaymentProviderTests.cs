// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ApplePay.Configuration;
using Bhengu.Finance.Payments.ApplePay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.ApplePay;

public class ApplePayPaymentProviderTests
{
    private const string ValidPkToken = """
        {"paymentData":{"version":"EC_v1","data":"abc","signature":"def","header":{}},"paymentMethod":{"displayName":"Visa 1234","network":"Visa","type":"debit"},"transactionIdentifier":"TX-1"}
        """;

    private static ApplePayPaymentProvider CreateProvider(
        IServiceCollection? services = null,
        ApplePayOptions? opts = null)
    {
        opts ??= new ApplePayOptions { MerchantId = "merchant.com.test", DownstreamProcessor = "fake" };
        services ??= new ServiceCollection();
        var sp = services.BuildServiceProvider();
        return new ApplePayPaymentProvider(sp, Options.Create(opts), NullLogger<ApplePayPaymentProvider>.Instance);
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new ApplePayPaymentProvider(
                new ServiceCollection().BuildServiceProvider(),
                Options.Create(new ApplePayOptions { DownstreamProcessor = "stripe" }),
                NullLogger<ApplePayPaymentProvider>.Instance));
        Assert.Contains("MerchantId", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenDownstreamProcessorMissing()
    {
        var ex = Assert.Throws<ProviderConfigurationException>(() =>
            new ApplePayPaymentProvider(
                new ServiceCollection().BuildServiceProvider(),
                Options.Create(new ApplePayOptions { MerchantId = "x", DownstreamProcessor = "" }),
                NullLogger<ApplePayPaymentProvider>.Instance));
        Assert.Contains("DownstreamProcessor", ex.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenTokenIsEmpty()
    {
        var provider = CreateProvider();
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPaymentAsync(SampleRequest("")));
        Assert.Equal("missing_token", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenTokenIsNotJson()
    {
        var provider = CreateProvider();
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPaymentAsync(SampleRequest("not-json")));
        Assert.Equal("invalid_token_json", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenTokenMissingRequiredFields()
    {
        var provider = CreateProvider();
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPaymentAsync(SampleRequest("""{"foo":"bar"}""")));
        Assert.Equal("invalid_token_shape", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenDownstreamProcessorNotRegistered()
    {
        var provider = CreateProvider();
        var ex = await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
            provider.ProcessPaymentAsync(SampleRequest(ValidPkToken)));
        Assert.Contains("DownstreamProcessor='fake'", ex.Message);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ForwardsToDownstream_WithEnrichedMetadata()
    {
        var fake = new FakeDownstreamProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IPaymentGatewayProvider>(fake);
        var provider = CreateProvider(
            services,
            new ApplePayOptions { MerchantId = "merchant.com.test", DownstreamProcessor = "fake-downstream" });

        var response = await provider.ProcessPaymentAsync(SampleRequest(ValidPkToken));

        Assert.Equal("FAKE-REF-1", response.GatewayReference);
        Assert.NotNull(fake.LastRequest);
        Assert.Equal("apple_pay", fake.LastRequest!.Metadata!["payment_source"]);
        Assert.Equal("applepay", fake.LastRequest.Metadata["original_provider"]);
        Assert.Equal("merchant.com.test", fake.LastRequest.Metadata["apple_merchant_id"]);
    }

    [Fact]
    public async Task ProcessRefundAsync_ForwardsToDownstream()
    {
        var fake = new FakeDownstreamProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IPaymentGatewayProvider>(fake);
        var provider = CreateProvider(
            services,
            new ApplePayOptions { MerchantId = "merchant.com.test", DownstreamProcessor = "fake-downstream" });

        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "PI-99",
            Amount = 10m,
            Reason = "Test"
        });

        Assert.Equal("REFUND-PI-99", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Refunded, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_AlwaysReturnsFalse_BecauseApplePayHasNoWebhooks()
    {
        var provider = CreateProvider();
        Assert.False(provider.VerifyWebhookSignature("anything", "anything"));
    }

    private static PaymentRequest SampleRequest(string token) => new()
    {
        PaymentMethodToken = token,
        Amount = 100m,
        Currency = "ZAR",
        Description = "Apple Pay test"
    };

    private sealed class FakeDownstreamProvider : IPaymentGatewayProvider
    {
        public string ProviderName => "fake-downstream";
        public Bhengu.Finance.Payments.Core.ProviderCapabilities Capabilities =>
            Bhengu.Finance.Payments.Core.ProviderCapabilities.Charge | Bhengu.Finance.Payments.Core.ProviderCapabilities.Refund;
        public PaymentRequest? LastRequest { get; private set; }

        public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(new PaymentResponse
            {
                GatewayReference = "FAKE-REF-1",
                Status = PaymentStatus.Completed,
                Amount = request.Amount,
                Currency = request.Currency
            });
        }

        public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default) =>
            Task.FromResult(new RefundResponse
            {
                GatewayReference = $"REFUND-{request.GatewayReference}",
                Amount = request.Amount,
                Status = PaymentStatus.Refunded
            });

        public bool VerifyWebhookSignature(string payload, string signature) => true;
        public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) =>
            Task.FromResult<WebhookEvent?>(null);
    }
}
