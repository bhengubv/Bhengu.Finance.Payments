// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.Alipay.Configuration;
using Bhengu.Finance.Payments.Alipay.Providers;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Bhengu.Finance.Payments.WeChatPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Depth;

// Wave C — Asian-rail providers. UnionPay / WeChatPay / Alipay use RSA verification, so
// every Create() seeds a shared in-process RSA keypair. Same five malformed cases plus the
// 1-MB nested check.

public sealed class Razorpay_MalformedJsonTests
{
    private static RazorpayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new RazorpayOptions
            {
                KeyId = "rzp_test_xx", KeySecret = "secret_xx",
                WebhookSecret = "wh", RazorpayXAccountNumber = "2323230099089860"
            }),
            NullLogger<RazorpayPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        _ = await provider.ParseWebhookAsync(payload);
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(MalformedJsonWaveA.BuildHugeNestedJson()),
            TimeSpan.FromSeconds(5));
    }
}

public sealed class Paytm_MalformedJsonTests
{
    private static PaytmPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaytmOptions
            {
                MerchantId = "TESTMERCHANT01", MerchantKey = "test_merchant_key_super_secret",
                WebsiteName = "WEBSTAGING", CallbackUrl = "https://example.com/cb", UseSandbox = true
            }),
            NullLogger<PaytmPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        _ = await provider.ParseWebhookAsync(payload);
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(MalformedJsonWaveA.BuildHugeNestedJson()),
            TimeSpan.FromSeconds(5));
    }
}

public sealed class PayUIndia_MalformedJsonTests
{
    private static PayUIndiaPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PayUIndiaOptions
            {
                MerchantKey = "gtKFFx", Salt = "eCwWELxi",
                SuccessUrl = "https://example.com/s", FailureUrl = "https://example.com/f"
            }),
            NullLogger<PayUIndiaPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        _ = await provider.ParseWebhookAsync(payload);
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(MalformedJsonWaveA.BuildHugeNestedJson()),
            TimeSpan.FromSeconds(5));
    }
}

public sealed class UnionPay_MalformedJsonTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string SignPriv = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());
    private static readonly string VerifyPub = Convert.ToBase64String(SharedRsa.ExportSubjectPublicKeyInfo());

    private static UnionPayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new UnionPayOptions
            {
                MerId = "777290058110097", CertId = "68759585097",
                SignCertPrivateKey = SignPriv, VerifyCertPublicKey = VerifyPub,
                FrontUrl = "https://example.com/r", BackUrl = "https://example.com/n",
                Currency = "156", Encoding = "UTF-8", UseSandbox = true
            }),
            NullLogger<UnionPayPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        _ = await provider.ParseWebhookAsync(payload);
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(MalformedJsonWaveA.BuildHugeNestedJson()),
            TimeSpan.FromSeconds(5));
    }
}

public sealed class WeChatPay_MalformedJsonTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string MerchantPriv = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());
    private static readonly string PlatformPub = Convert.ToBase64String(SharedRsa.ExportSubjectPublicKeyInfo());

    private static WeChatPayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new WeChatPayOptions
            {
                AppId = "wxAPPID", MerchantId = "1900000001", MerchantCertSerialNo = "ABCDEF1234567890",
                MerchantPrivateKey = MerchantPriv, V3ApiKey = "12345678901234567890123456789012",
                WeChatPayPlatformCertificate = PlatformPub,
                NotifyUrl = "https://example.com/wechat-notify", Currency = "CNY"
            }),
            NullLogger<WeChatPayPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        _ = await provider.ParseWebhookAsync(payload);
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(MalformedJsonWaveA.BuildHugeNestedJson()),
            TimeSpan.FromSeconds(5));
    }
}

public sealed class Alipay_MalformedJsonTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string MerchantPriv = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());
    private static readonly string AlipayPub = Convert.ToBase64String(SharedRsa.ExportSubjectPublicKeyInfo());

    private static AlipayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new AlipayOptions
            {
                ClientId = "ALIPAY_TEST", MerchantPrivateKey = MerchantPriv, AlipayPublicKey = AlipayPub,
                NotifyUrl = "https://example.com/wh", RedirectUrl = "https://example.com/r",
                Currency = "USD", UseSandbox = true
            }),
            NullLogger<AlipayPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        _ = await provider.ParseWebhookAsync(payload);
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(MalformedJsonWaveA.BuildHugeNestedJson()),
            TimeSpan.FromSeconds(5));
    }
}
