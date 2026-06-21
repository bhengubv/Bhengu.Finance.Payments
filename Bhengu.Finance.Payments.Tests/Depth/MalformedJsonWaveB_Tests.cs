// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Providers;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Providers;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Providers;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Bhengu.Finance.Payments.MTNMoMo.Providers;
using Bhengu.Finance.Payments.OrangeMoney.Configuration;
using Bhengu.Finance.Payments.OrangeMoney.Providers;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Providers;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Bhengu.Finance.Payments.Stitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Depth;

// Wave B — OAuth-fronted providers (mobile-money, bank rails). These providers do an OAuth
// fetch on the first business call, but ParseWebhookAsync is purely-CPU so no token is needed.
// Same five malformed-input cases as Wave A; same "no throw, no crash" contract.

public sealed class Stitch_MalformedJsonTests
{
    private static StitchPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new StitchOptions
            {
                ClientId = "c", ApiKey = "k", WebhookSecret = "wh",
                BeneficiaryAccountNumber = "1", BeneficiaryBankId = "fnb",
                BeneficiaryName = "n", Currency = "ZAR"
            }),
            NullLogger<StitchPaymentProvider>.Instance);

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

public sealed class MPesa_MalformedJsonTests
{
    private static MPesaPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new MPesaOptions
            {
                ConsumerKey = "ck", ConsumerSecret = "cs", BusinessShortCode = "174379",
                Passkey = "bfb279f9aa9bdbcf158e97dd71a467cd2e0c893059b10f78e6b72ada1ed2c919",
                CallbackUrl = "https://example.com/cb/tok123", CallbackUrlToken = "tok123",
                InitiatorName = "testapi", SecurityCredential = "Safaricom999!*!",
                QueueTimeoutUrl = "https://example.com/timeout", ResultUrl = "https://example.com/result",
                UseSandbox = true
            }),
            NullLogger<MPesaPaymentProvider>.Instance);

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

public sealed class MTNMoMo_MalformedJsonTests
{
    private static MTNMoMoPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new MTNMoMoOptions
            {
                SubscriptionKey = "sub-key",
                ApiUserId = "00000000-0000-0000-0000-000000000001",
                ApiKey = "api-key-secret",
                TargetEnvironment = "sandbox",
                CallbackUrl = "https://example.com/momo/cb",
                UseSandbox = true
            }),
            NullLogger<MTNMoMoPaymentProvider>.Instance);

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

public sealed class AirtelMoney_MalformedJsonTests
{
    private static AirtelMoneyPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new AirtelMoneyOptions
            {
                ClientId = "c", ClientSecret = "s", Country = "KE", Currency = "KES",
                CallbackUrl = "https://example.com/cb", WebhookSecret = "wh", UseSandbox = true
            }),
            NullLogger<AirtelMoneyPaymentProvider>.Instance);

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

public sealed class OrangeMoney_MalformedJsonTests
{
    private static OrangeMoneyPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new OrangeMoneyOptions
            {
                ConsumerKey = "ck", ConsumerSecret = "cs", MerchantKey = "mk", Country = "ci",
                ReturnUrl = "https://example.com/r", CancelUrl = "https://example.com/c",
                NotifUrl = "https://example.com/n", UseSandbox = true
            }),
            NullLogger<OrangeMoneyPaymentProvider>.Instance);

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

public sealed class Cellulant_MalformedJsonTests
{
    private static CellulantPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new CellulantOptions
            {
                ServiceCode = "TGNTEST", ClientId = "c", ClientSecret = "s",
                WebhookSecret = "wh", CallbackUrl = "https://example.com/cb", UseSandbox = true
            }),
            NullLogger<CellulantPaymentProvider>.Instance);

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

public sealed class Interswitch_MalformedJsonTests
{
    private static InterswitchPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new InterswitchOptions
            {
                ClientId = "c", ClientSecret = "s", MerchantCode = "MX12345",
                ProductId = "10101", WebhookSecret = "wh"
            }),
            NullLogger<InterswitchPaymentProvider>.Instance);

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

public sealed class Pesapal_MalformedJsonTests
{
    private static PesapalPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PesapalOptions
            {
                ConsumerKey = "ck", ConsumerSecret = "cs", IpnId = "ipn-id-123",
                CallbackUrl = "https://example.com/r", Currency = "KES"
            }),
            NullLogger<PesapalPaymentProvider>.Instance);

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
