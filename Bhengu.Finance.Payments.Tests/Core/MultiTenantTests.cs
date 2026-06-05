// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.MultiTenant;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Verifies cross-tenant isolation guarantees of <see cref="MultiTenantPaymentGatewayProvider{TProvider, TOptions}"/>.
/// Includes the AES-GCM cipher round-trip + the no-tenant-in-scope guard.
/// </summary>
public class MultiTenantTests
{
    private sealed class FakeOptions
    {
        public string SecretKey { get; set; } = "";
    }

    private sealed class FakeProvider(IOptions<FakeOptions> opts) : IPaymentGatewayProvider
    {
        public string CapturedSecret { get; } = opts.Value.SecretKey;
        public string ProviderName => "fake";
        public ProviderCapabilities Capabilities => ProviderCapabilities.Charge;
        public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest r, CancellationToken ct = default)
            => Task.FromResult(new PaymentResponse { GatewayReference = "ref:" + CapturedSecret, Status = PaymentStatus.Completed, Amount = r.Amount, Currency = r.Currency });
        public Task<RefundResponse> ProcessRefundAsync(RefundRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public bool VerifyWebhookSignature(string payload, string signature) => true;
        public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) => Task.FromResult<WebhookEvent?>(null);
    }

    private sealed class StubTenantContext(string? tenant) : IBhenguTenantContext
    {
        public string? Tenant { get; set; } = tenant;
        public string CurrentTenantId => Tenant ?? throw new InvalidOperationException("No tenant");
        public bool HasTenant => Tenant is not null;
    }

    private sealed class StubSecretsStore : ITenantPaymentSecretsStore
    {
        public Dictionary<string, FakeOptions> ByTenant { get; } = new();
        public Task<T?> GetOptionsAsync<T>(string tenantId, CancellationToken ct = default) where T : class, new()
        {
            if (typeof(T) == typeof(FakeOptions) && ByTenant.TryGetValue(tenantId, out var o))
                return Task.FromResult<T?>(o as T);
            return Task.FromResult<T?>(null);
        }
    }

    [Fact]
    public async Task DifferentTenantsDoNotShareSecrets()
    {
        var ctx = new StubTenantContext("tenant-A");
        var store = new StubSecretsStore();
        store.ByTenant["tenant-A"] = new FakeOptions { SecretKey = "secret-A" };
        store.ByTenant["tenant-B"] = new FakeOptions { SecretKey = "secret-B" };

        var wrapper = new MultiTenantPaymentGatewayProvider<FakeProvider, FakeOptions>(
            "fake", ProviderCapabilities.Charge, ctx, store,
            opts => new FakeProvider(opts),
            NullLogger<MultiTenantPaymentGatewayProvider<FakeProvider, FakeOptions>>.Instance);

        var responseA = await wrapper.ProcessPaymentAsync(new PaymentRequest { PaymentMethodToken = "t", Amount = 1, Currency = "ZAR", Description = "" });

        ctx.Tenant = "tenant-B";
        var responseB = await wrapper.ProcessPaymentAsync(new PaymentRequest { PaymentMethodToken = "t", Amount = 1, Currency = "ZAR", Description = "" });

        Assert.Equal("ref:secret-A", responseA.GatewayReference);
        Assert.Equal("ref:secret-B", responseB.GatewayReference);
    }

    [Fact]
    public async Task ThrowsWhenNoTenantInScope()
    {
        var wrapper = new MultiTenantPaymentGatewayProvider<FakeProvider, FakeOptions>(
            "fake", ProviderCapabilities.Charge, new StubTenantContext(null), new StubSecretsStore(),
            opts => new FakeProvider(opts),
            NullLogger<MultiTenantPaymentGatewayProvider<FakeProvider, FakeOptions>>.Instance);

        await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
            wrapper.ProcessPaymentAsync(new PaymentRequest { PaymentMethodToken = "t", Amount = 1, Currency = "ZAR", Description = "" }));
    }

    [Fact]
    public async Task ThrowsWhenTenantHasNotConfiguredProvider()
    {
        var ctx = new StubTenantContext("orphan-tenant");
        var store = new StubSecretsStore(); // empty — no options for any tenant

        var wrapper = new MultiTenantPaymentGatewayProvider<FakeProvider, FakeOptions>(
            "fake", ProviderCapabilities.Charge, ctx, store,
            opts => new FakeProvider(opts),
            NullLogger<MultiTenantPaymentGatewayProvider<FakeProvider, FakeOptions>>.Instance);

        var ex = await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
            wrapper.ProcessPaymentAsync(new PaymentRequest { PaymentMethodToken = "t", Amount = 1, Currency = "ZAR", Description = "" }));
        Assert.Contains("orphan-tenant", ex.Message);
    }

    [Fact]
    public void AesGcmCipher_RoundTripsPlaintext()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        var cipher = new AesGcmPaymentSecretsCipher(key);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("""{"SecretKey":"sk_live_abc123"}""");
        var ad = System.Text.Encoding.UTF8.GetBytes("tenant-A:PayFastOptions");

        var ct = cipher.Encrypt(plaintext, ad);
        var decrypted = cipher.Decrypt(ct, ad);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void AesGcmCipher_ThrowsWhenAssociatedDataDiffers()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        var cipher = new AesGcmPaymentSecretsCipher(key);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("secret");
        var ad1 = System.Text.Encoding.UTF8.GetBytes("tenant-A");
        var ad2 = System.Text.Encoding.UTF8.GetBytes("tenant-B");

        var ct = cipher.Encrypt(plaintext, ad1);
        // Decrypt with wrong AD — must throw, otherwise tenant-mixup attack is possible
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => cipher.Decrypt(ct, ad2));
    }

    [Fact]
    public void AesGcmCipher_ThrowsOnTamperedCiphertext()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        var cipher = new AesGcmPaymentSecretsCipher(key);

        var plaintext = System.Text.Encoding.UTF8.GetBytes("secret");
        var ad = System.Text.Encoding.UTF8.GetBytes("ctx");

        var ct = cipher.Encrypt(plaintext, ad);
        ct[ct.Length - 1] ^= 0xFF; // flip last byte (tag region)

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => cipher.Decrypt(ct, ad));
    }

    [Fact]
    public void AesGcmCipher_RejectsWrongKeyLength()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmPaymentSecretsCipher(new byte[16]));  // AES-128 key, not 32
        Assert.Throws<ArgumentException>(() => new AesGcmPaymentSecretsCipher(new byte[64]));  // too long
    }
}
