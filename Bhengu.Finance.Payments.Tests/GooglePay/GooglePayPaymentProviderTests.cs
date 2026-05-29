// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Google.Configuration;
using Bhengu.Finance.Payments.Google.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.GooglePay;

public class GooglePayPaymentProviderTests
{
    private const string ValidGPayToken = """
        {"protocolVersion":"ECv2","signature":"abc","signedMessage":"{\"encryptedMessage\":\"...\"}"}
        """;

    private static GooglePayPaymentProvider CreateProvider(
        IServiceCollection? services = null,
        GooglePayOptions? opts = null)
    {
        opts ??= new GooglePayOptions { MerchantId = "GMERCH-123", DownstreamProcessor = "fake" };
        services ??= new ServiceCollection();
        var sp = services.BuildServiceProvider();
        return new GooglePayPaymentProvider(sp, Options.Create(opts), NullLogger<GooglePayPaymentProvider>.Instance);
    }

    [Fact]
    public void Constructor_Throws_WhenMerchantIdMissing()
    {
        Assert.Throws<ProviderConfigurationException>(() =>
            new GooglePayPaymentProvider(
                new ServiceCollection().BuildServiceProvider(),
                Options.Create(new GooglePayOptions { DownstreamProcessor = "stripe" }),
                NullLogger<GooglePayPaymentProvider>.Instance));
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
    public async Task ProcessPaymentAsync_Throws_WhenTokenMissingSignedMessage()
    {
        var provider = CreateProvider();
        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() =>
            provider.ProcessPaymentAsync(SampleRequest("""{"signature":"x"}""")));
        Assert.Equal("invalid_token_shape", ex.ProviderErrorCode);
    }

    [Fact]
    public async Task ProcessPaymentAsync_ForwardsToDownstream_WithEnrichedMetadata()
    {
        var fake = new FakeDownstreamProvider();
        var services = new ServiceCollection();
        services.AddSingleton<IPaymentGatewayProvider>(fake);
        var provider = CreateProvider(
            services,
            new GooglePayOptions { MerchantId = "GMERCH-123", DownstreamProcessor = "fake-downstream", UseTestEnvironment = true });

        var response = await provider.ProcessPaymentAsync(SampleRequest(ValidGPayToken));

        Assert.Equal("FAKE-REF-1", response.GatewayReference);
        Assert.Equal("google_pay", fake.LastRequest!.Metadata!["payment_source"]);
        Assert.Equal("googlepay", fake.LastRequest.Metadata["original_provider"]);
        Assert.Equal("TEST", fake.LastRequest.Metadata["google_environment"]);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenDownstreamProcessorNotRegistered()
    {
        var provider = CreateProvider();
        var ex = await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
            provider.ProcessPaymentAsync(SampleRequest(ValidGPayToken)));
        Assert.Contains("DownstreamProcessor='fake'", ex.Message);
    }

    private static PaymentRequest SampleRequest(string token) => new()
    {
        PaymentMethodToken = token,
        Amount = 50m,
        Currency = "ZAR",
        Description = "Google Pay test"
    };

    private sealed class FakeDownstreamProvider : IPaymentGatewayProvider
    {
        public string ProviderName => "fake-downstream";
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
