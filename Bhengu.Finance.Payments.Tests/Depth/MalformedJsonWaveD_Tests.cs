// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.ChipperCash.Providers;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.CMI.Providers;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.DPO.Configuration;
using Bhengu.Finance.Payments.DPO.Providers;
using Bhengu.Finance.Payments.EcoCash.Configuration;
using Bhengu.Finance.Payments.EcoCash.Providers;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Bhengu.Finance.Payments.ExpressPay.Providers;
using Bhengu.Finance.Payments.Fawry.Configuration;
using Bhengu.Finance.Payments.Fawry.Providers;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Providers;
using Bhengu.Finance.Payments.IPay.Configuration;
using Bhengu.Finance.Payments.IPay.Providers;
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Bhengu.Finance.Payments.JamboPay.Providers;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Bhengu.Finance.Payments.Kashier.Providers;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Providers;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Internals;
using Bhengu.Finance.Payments.Mukuru.Providers;
using Bhengu.Finance.Payments.Onafriq.Configuration;
using Bhengu.Finance.Payments.Onafriq.Providers;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Providers;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Internals;
using Bhengu.Finance.Payments.Ozow.Providers;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Providers;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Bhengu.Finance.Payments.PayJustNow.Providers;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Providers;
using Bhengu.Finance.Payments.Slydepay.Configuration;
using Bhengu.Finance.Payments.Slydepay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Depth;

// Wave D — remaining HTTP-bound providers. Same five malformed cases plus the 1-MB nested check.

public sealed class ChipperCash_MalformedJsonTests
{
    private static ChipperCashPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new ChipperCashOptions
            {
                ApiKey = "k", ApiSecret = "s", MerchantId = "M",
                CallbackUrl = "https://example.com/cb", Country = "NG", Currency = "NGN"
            }),
            NullLogger<ChipperCashPaymentProvider>.Instance,
            null);

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

public sealed class DPO_MalformedJsonTests
{
    private static DPOPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new DPOOptions
            {
                CompanyToken = "DPO_TEST_COMPANY_TOKEN", ServiceType = "3854",
                ServiceDescription = "Online Test", RedirectUrl = "https://example.com/r",
                BackUrl = "https://example.com/b", UseSandbox = true
            }),
            NullLogger<DPOPaymentProvider>.Instance,
            null);

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

public sealed class EcoCash_MalformedJsonTests
{
    private static EcoCashPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new EcoCashOptions
            {
                ApiKey = "k", Username = "u", Password = "p", MerchantCode = "MC",
                MerchantPin = "1234", MerchantNumber = "263772000000",
                NotifyUrl = "https://example.com/n", UseSandbox = true
            }),
            NullLogger<EcoCashPaymentProvider>.Instance,
            null);

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

public sealed class ExpressPay_MalformedJsonTests
{
    private static ExpressPayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new ExpressPayOptions
            {
                MerchantId = "demo-merchant", ApiKey = "demo-api-key",
                RedirectUrl = "https://example.com/r", PostUrl = "https://example.com/p", Currency = "GHS"
            }),
            NullLogger<ExpressPayPaymentProvider>.Instance);

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

public sealed class Fawry_MalformedJsonTests
{
    private static FawryPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new FawryOptions { MerchantCode = "MERCH_1", SecurityKey = "sk_fawry_test", UseSandbox = true }),
            NullLogger<FawryPaymentProvider>.Instance);

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

public sealed class Hubtel_MalformedJsonTests
{
    private static HubtelPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new HubtelOptions
            {
                ClientId = "ci", ClientSecret = "cs", MerchantAccountNumber = "1234567",
                WebhookSecret = "whsec", CallbackUrl = "https://example.com/cb",
                ReturnUrl = "https://example.com/r", Currency = "GHS"
            }),
            NullLogger<HubtelPaymentProvider>.Instance);

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

public sealed class IPay_MalformedJsonTests
{
    private static IPayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new IPayOptions
            {
                VendorId = "demo", HashKey = "demoCHANGED", Live = "1", Currency = "KES",
                CallbackUrl = "https://example.com/cb"
            }),
            NullLogger<IPayPaymentProvider>.Instance);

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

public sealed class JamboPay_MalformedJsonTests
{
    private static JamboPayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new JamboPayOptions
            {
                ApiKey = "k", ClientId = "cid", ClientSecret = "csec", MerchantCode = "MCH-001",
                WebhookSecret = "wh", CallbackUrl = "https://example.com/r", Currency = "KES"
            }),
            NullLogger<JamboPayPaymentProvider>.Instance);

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

public sealed class Kashier_MalformedJsonTests
{
    private static KashierPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new KashierOptions
            {
                ApiKey = "api_test_key", MerchantId = "MID_1", SecretKey = "secret_kashier",
                WebhookSecret = "wh", Currency = "EGP", Mode = "test", UseSandbox = true
            }),
            NullLogger<KashierPaymentProvider>.Instance,
            new KashierIdempotencyCache(new InMemoryBhenguDistributedCache()));

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

public sealed class MercadoPago_MalformedJsonTests
{
    private static MercadoPagoPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new MercadoPagoOptions { AccessToken = "TEST-1234567890", WebhookSecret = "wh" }),
            NullLogger<MercadoPagoPaymentProvider>.Instance);

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

public sealed class Moniepoint_MalformedJsonTests
{
    private static MoniepointPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new MoniepointOptions
            {
                ApiKey = "mpt-api-key", SecretKey = "sk", ContractCode = "CONTRACT-1",
                WebhookSecret = "wh", RedirectUrl = "https://example.com/r"
            }),
            NullLogger<MoniepointPaymentProvider>.Instance);

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

public sealed class Mukuru_MalformedJsonTests
{
    private static MukuruPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new MukuruOptions
            {
                ClientId = "c", ClientSecret = "s", MerchantId = "M",
                WebhookSecret = "wh", SenderCountry = "ZA", DefaultCurrency = "ZAR",
                CallbackUrl = "https://example.com/cb"
            }),
            NullLogger<MukuruPaymentProvider>.Instance,
            new MukuruIdempotencyCache(new InMemoryBhenguDistributedCache()));

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

public sealed class Onafriq_MalformedJsonTests
{
    private static OnafriqPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new OnafriqOptions
            {
                ApiKey = "onafriq_test_key", MerchantId = "MERCH-001", WebhookSecret = "wh",
                CallbackUrl = "https://example.com/cb", UseSandbox = true
            }),
            NullLogger<OnafriqPaymentProvider>.Instance,
            null);

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

public sealed class OPay_MalformedJsonTests
{
    private static OPayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new OPayOptions
            {
                PublicKey = "opay-pub-key", SecretKey = "opay-secret-key", MerchantId = "MERCH123",
                Country = "NG", CallbackUrl = "https://example.com/cb", ReturnUrl = "https://example.com/r"
            }),
            NullLogger<OPayPaymentProvider>.Instance);

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

public sealed class Ozow_MalformedJsonTests
{
    private static OzowPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new OzowOptions { SiteCode = "TEST", PrivateKey = "priv", ApiKey = "apik", UseSandbox = true }),
            NullLogger<OzowPaymentProvider>.Instance,
            new OzowIdempotencyCache(new InMemoryBhenguDistributedCache()));

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

public sealed class PagSeguro_MalformedJsonTests
{
    private static PagSeguroPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PagSeguroOptions { ApiToken = "pagbank-test-token", WebhookSecret = "wh" }),
            NullLogger<PagSeguroPaymentProvider>.Instance);

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

public sealed class PayFast_MalformedJsonTests
{
    private static PayFastPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PayFastOptions
            {
                MerchantId = "10000100", MerchantKey = "46f0cd694581a",
                Passphrase = "jt7NOE43FZPn", UseSandbox = true
            }),
            NullLogger<PayFastPaymentProvider>.Instance);

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

public sealed class PayJustNow_MalformedJsonTests
{
    private static PayJustNowPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PayJustNowOptions
            {
                ApiKey = "k", SecretKey = "wh-secret", MerchantId = "m-1", UseSandbox = true
            }),
            NullLogger<PayJustNowPaymentProvider>.Instance,
            new PayJustNowIdempotencyCache(new InMemoryBhenguDistributedCache()));

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

public sealed class Remita_MalformedJsonTests
{
    private static RemitaPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new RemitaOptions
            {
                MerchantId = "2547916", ServiceTypeId = "4430731", ApiKey = "1946", ApiToken = "tok",
                FromBank = "044", DebitAccount = "0690000031", Currency = "NGN",
                CallbackUrl = "https://example.com/cb"
            }),
            NullLogger<RemitaPaymentProvider>.Instance);

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

public sealed class Slydepay_MalformedJsonTests
{
    private static SlydepayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new SlydepayOptions
            {
                EmailOrMobile = "merchant@example.com", MerchantKey = "mkey-123",
                Currency = "GHS", PaymentChannels = "7", CallbackUrl = "https://example.com/cb"
            }),
            NullLogger<SlydepayPaymentProvider>.Instance);

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

public sealed class CMI_MalformedJsonTests
{
    private static CMIPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new CMIOptions
            {
                ClientId = "600000001", StoreKey = "cmi_storekey_test",
                ApiUser = "api_user", ApiPassword = "api_password",
                OkUrl = "https://example.com/ok", FailUrl = "https://example.com/f",
                CallbackUrl = "https://example.com/cb", Currency = "504", Lang = "en", UseSandbox = true
            }),
            NullLogger<CMIPaymentProvider>.Instance);

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

public sealed class BricsPay_MalformedJsonTests
{
    private static BricsPayPaymentProvider Create()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new BricsPayOptions
            {
                TerminalId = "POS-1", BaseUrl = "https://terminal.brics.example",
                PrivateKeyPem = ec.ExportPkcs8PrivateKeyPem()
            }),
            NullLogger<BricsPayPaymentProvider>.Instance);
    }

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public void ParseCallback_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        _ = provider.ParseCallback(payload);
    }

    [Fact]
    public async Task ParseCallback_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            () => { _ = provider.ParseCallback(MalformedJsonWaveA.BuildHugeNestedJson()); return Task.CompletedTask; },
            TimeSpan.FromSeconds(5));
    }
}
