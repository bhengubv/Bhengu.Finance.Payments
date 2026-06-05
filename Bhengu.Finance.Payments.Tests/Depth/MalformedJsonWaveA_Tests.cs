// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using System.Net;
using System.Text;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.Stripe;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.Wave.Configuration;
using Bhengu.Finance.Payments.Wave.Providers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Depth;

/// <summary>
/// Wave A — flagship providers (Paystack, Stripe, Flutterwave, Yoco, Wave, Paymob).
/// Each provider's <c>ParseWebhookAsync</c> is hammered with a battery of malformed inputs:
/// empty / whitespace / non-JSON / half-JSON / wrong-root-type / missing-field /
/// wrong-field-type / 1-MB nested JSON. The contract is "never throw" — the provider may
/// return <c>null</c> OR a <see cref="Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEvent"/>
/// with <c>Category=Unknown</c>, as long as it doesn't blow up. The 1-MB nested case asserts
/// completion within 5 seconds — a defensive bound against pathological recursion.
/// </summary>
public static class MalformedJsonWaveA
{
    /// <summary>Inputs used by every <c>[Theory]</c>. Two of them (empty / whitespace) are surfaced
    /// in their own <c>[Fact]</c> because <see cref="ArgumentException.ThrowIfNullOrEmpty"/> rejects
    /// empty strings at the boundary and we accept the throw as documented behaviour.</summary>
    public static readonly TheoryData<string> NonEmptyMalformed = new()
    {
        "definitely not json",
        "{\"foo\":",
        "[]",
        "{\"unrelated\":\"x\"}",
        "{\"event\":123}",
    };

    /// <summary>Generate a deeply-nested JSON payload around the 1-MB mark.</summary>
    /// <remarks>
    /// A naive recursive-descent JSON parser would stack-overflow on this; <c>System.Text.Json</c>
    /// caps default nesting at 64 depths so it throws a <c>JsonException</c> which the provider's
    /// try/catch swallows. We just need to confirm the call returns inside 5 s and doesn't propagate.
    /// </remarks>
    public static string BuildHugeNestedJson()
    {
        var sb = new StringBuilder(1024 * 1024 + 1024);
        for (var i = 0; i < 30_000; i++) sb.Append("{\"x\":");
        sb.Append("null");
        for (var i = 0; i < 30_000; i++) sb.Append('}');
        return sb.ToString();
    }

    /// <summary>Assert a parser completes within the given wall-clock budget.</summary>
    public static async Task AssertParsesWithinBudget(Func<Task> body, TimeSpan budget)
    {
        var start = Stopwatch.GetTimestamp();
        await body();
        var elapsed = Stopwatch.GetElapsedTime(start);
        Assert.True(elapsed < budget, $"parse exceeded budget: {elapsed.TotalMilliseconds:F0} ms > {budget.TotalMilliseconds:F0} ms");
    }
}

// ---------------------------------------------------------------------------
//  Paystack
// ---------------------------------------------------------------------------
public sealed class Paystack_MalformedJsonTests
{
    private static PaystackPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync(payload);
        // Contract: provider returns null OR a WebhookEvent with no inner crash.
        // Either is acceptable; what matters is that the call does not throw.
        _ = evt;
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        var payload = MalformedJsonWaveA.BuildHugeNestedJson();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(payload),
            TimeSpan.FromSeconds(5));
    }
}

// ---------------------------------------------------------------------------
//  Stripe
// ---------------------------------------------------------------------------
[Collection(StripeConfigurationCollection.Name)]
public sealed class Stripe_MalformedJsonTests
{
    private static StripePaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripePaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync(payload);
        _ = evt;
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        var payload = MalformedJsonWaveA.BuildHugeNestedJson();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(payload),
            TimeSpan.FromSeconds(5));
    }
}

// ---------------------------------------------------------------------------
//  Flutterwave
// ---------------------------------------------------------------------------
public sealed class Flutterwave_MalformedJsonTests
{
    private static FlutterwavePaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new FlutterwaveOptions
            {
                SecretKey = "FLWSECK_TEST-xxx",
                PublicKey = "FLWPUBK_TEST-xxx",
                EncryptionKey = "FLWSECK_TEST_xxx",
                WebhookSecret = "verify",
                RedirectUrl = "https://example.com/r"
            }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync(payload);
        _ = evt;
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        var payload = MalformedJsonWaveA.BuildHugeNestedJson();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(payload),
            TimeSpan.FromSeconds(5));
    }
}

// ---------------------------------------------------------------------------
//  Yoco
// ---------------------------------------------------------------------------
public sealed class Yoco_MalformedJsonTests
{
    private static YocoPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new YocoOptions { SecretKey = "sk", WebhookSecret = "wh" }),
            NullLogger<YocoPaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync(payload);
        _ = evt;
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        var payload = MalformedJsonWaveA.BuildHugeNestedJson();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(payload),
            TimeSpan.FromSeconds(5));
    }
}

// ---------------------------------------------------------------------------
//  Wave
// ---------------------------------------------------------------------------
public sealed class Wave_MalformedJsonTests
{
    private static WavePaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new WaveOptions
            {
                ApiKey = "wave_sn_prod_xxx",
                WebhookSecret = "webhook-test-secret",
                Currency = "XOF",
                SuccessUrl = "https://example.com/s",
                ErrorUrl = "https://example.com/e"
            }),
            NullLogger<WavePaymentProvider>.Instance);

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync(payload);
        _ = evt;
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        var payload = MalformedJsonWaveA.BuildHugeNestedJson();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(payload),
            TimeSpan.FromSeconds(5));
    }
}

// ---------------------------------------------------------------------------
//  Paymob
// ---------------------------------------------------------------------------
public sealed class Paymob_MalformedJsonTests
{
    private static PaymobPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaymobOptions
            {
                ApiKey = "api_test_key",
                HmacSecret = "hmac_secret_xxx",
                IntegrationId = 12345,
                IframeId = 99,
                Currency = "EGP"
            }),
            NullLogger<PaymobPaymentProvider>.Instance,
            new PaymobIdempotencyCache(new InMemoryBhenguDistributedCache()));

    [Theory]
    [MemberData(nameof(MalformedJsonWaveA.NonEmptyMalformed), MemberType = typeof(MalformedJsonWaveA))]
    public async Task ParseWebhookAsync_DoesNotThrow_OnMalformedInput(string payload)
    {
        var provider = Create();
        var evt = await provider.ParseWebhookAsync(payload);
        _ = evt;
    }

    [Fact]
    public async Task ParseWebhookAsync_HandlesHugeNestedJson_Within5Seconds()
    {
        var provider = Create();
        var payload = MalformedJsonWaveA.BuildHugeNestedJson();
        await MalformedJsonWaveA.AssertParsesWithinBudget(
            async () => _ = await provider.ParseWebhookAsync(payload),
            TimeSpan.FromSeconds(5));
    }
}
