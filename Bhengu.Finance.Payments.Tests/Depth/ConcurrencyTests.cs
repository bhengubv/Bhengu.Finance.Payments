// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Subscription;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Internals;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.Stripe;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.Wave.Configuration;
using Bhengu.Finance.Payments.Wave.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Depth;

/// <summary>
/// Concurrency stress for the three idempotency styles in the Bhengu portfolio:
/// <list type="bullet">
///   <item><description>Stripe — native server-side dedup via the <c>Idempotency-Key</c> request header.</description></item>
///   <item><description>Paystack — client-side dedup cache (no native header on the wire).</description></item>
///   <item><description>Wave — native server-side dedup via the <c>idempotency_key</c> body field on the Payout endpoint.</description></item>
/// </list>
/// <para>
/// For each style we fire 50 concurrent calls under the same idempotency key and verify the
/// shape promised by the contract:
/// </para>
/// <list type="number">
///   <item><description>Paystack collapses to one upstream call; Stripe and Wave send 50 (the dedup happens server-side, and Stripe's documented retry strategy DOES want every retry to reach the API with the same key).</description></item>
///   <item><description>Every caller receives a non-faulted response with the same <see cref="PaymentResponse.GatewayReference"/>.</description></item>
///   <item><description>No <see cref="AggregateException"/> / <see cref="InvalidOperationException"/> escapes the in-flight coalesce path.</description></item>
/// </list>
/// </summary>
[Collection(StripeConfigurationCollection.Name)]
public sealed class ConcurrencyTests
{
    private const int ConcurrentCallers = 50;

    // -----------------------------------------------------------------------
    //  Paystack — true client-side dedup. 50 concurrent → 1 HTTP call.
    // -----------------------------------------------------------------------

    /// <summary>
    /// 50 callers, same key, single cache: only ONE outbound HTTP request, and every
    /// caller observes the same <see cref="PaymentResponse.GatewayReference"/>.
    /// </summary>
    [Fact]
    public async Task Paystack_FiftyConcurrentCalls_SameKey_DedupesToOneHttpCall()
    {
        var handler = new CountingStubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"ok","data":{"id":1,"reference":"ref_concurrent_1","status":"success","amount":10000,"currency":"NGN"}}
                """));

        var cache = new PaystackIdempotencyCache();
        var provider = new PaystackPaymentProvider(
            new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance,
            cache);

        var request = new PaymentRequest
        {
            PaymentMethodToken = "AUTH_concurrent",
            Amount = 100m,
            Currency = "NGN",
            Description = "concurrent",
            IdempotencyKey = "concurrent-key-1"
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.ProcessPaymentAsync(request)));

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(ConcurrentCallers, results.Length);
        Assert.All(results, r => Assert.Equal("ref_concurrent_1", r.GatewayReference));
        // Reference equality is a stronger contract — coalesced callers should observe
        // the same materialised PaymentResponse instance from the cache.
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    /// <summary>
    /// Second wave under the SAME key (after the first wave has settled) is also served
    /// from the cache — still no extra upstream calls. Demonstrates the distributed-cache
    /// hit path complements the in-flight coalesce path.
    /// </summary>
    [Fact]
    public async Task Paystack_SecondWave_SameKey_StillNoExtraUpstreamCall()
    {
        var handler = new CountingStubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"id":99,"reference":"ref_wave_2","status":"success","amount":10000,"currency":"NGN"}}
                """));

        var cache = new PaystackIdempotencyCache();
        var provider = new PaystackPaymentProvider(
            new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance,
            cache);

        var request = new PaymentRequest
        {
            PaymentMethodToken = "AUTH_x",
            Amount = 100m,
            Currency = "NGN",
            Description = "wave",
            IdempotencyKey = "wave-key-1"
        };

        // First wave — collapses to a single upstream call.
        await Task.WhenAll(Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.ProcessPaymentAsync(request)));

        // Second wave under the same key — distributed cache hit, still 1.
        await Task.WhenAll(Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.ProcessPaymentAsync(request)));

        Assert.Equal(1, handler.CallCount);
    }

    // -----------------------------------------------------------------------
    //  Stripe — native Idempotency-Key header. Stripe.net retry policy needs
    //  every call to reach upstream, so 50 concurrent → 50 HTTP calls — but
    //  every one MUST carry the same Idempotency-Key header for Stripe's
    //  server-side dedup to kick in.
    // -----------------------------------------------------------------------

    /// <summary>
    /// 50 concurrent calls reach the upstream (no client-side coalesce). EVERY one of
    /// those 50 outbound requests carries the same <c>Idempotency-Key</c> header so
    /// Stripe's server-side dedup collapses them to a single charge. Asserting on the
    /// header is the strongest invariant we can test client-side; the actual
    /// dedup is an upstream behaviour.
    /// </summary>
    [Fact]
    public async Task Stripe_FiftyConcurrentCalls_SameKey_EveryRequestCarriesSameIdempotencyHeader()
    {
        var observedKeys = new List<string?>();
        var keyLock = new object();
        var handler = new CountingStubHttpMessageHandler((req, _) =>
        {
            var headerValue = req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;
            lock (keyLock) observedKeys.Add(headerValue);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_stripe_concurrent","object":"payment_intent","amount":10000,"currency":"usd","status":"succeeded"}
                """);
        });

        var provider = new StripePaymentProvider(
            new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripePaymentProvider>.Instance);

        var request = new PaymentRequest
        {
            PaymentMethodToken = "pm_card_visa",
            Amount = 100m,
            Currency = "USD",
            Description = "stripe-concurrent",
            IdempotencyKey = "stripe-concurrent-key"
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.ProcessPaymentAsync(request)));

        Assert.Equal(ConcurrentCallers, handler.CallCount);
        Assert.Equal(ConcurrentCallers, observedKeys.Count);
        Assert.All(observedKeys, k => Assert.Equal("stripe-concurrent-key", k));
        Assert.All(results, r => Assert.Equal("pi_stripe_concurrent", r.GatewayReference));
    }

    // -----------------------------------------------------------------------
    //  Wave Payout — native idempotency_key body field. Same pattern as Stripe
    //  (no client-side cache), but the dedup token rides in the JSON body of
    //  the v1/payout request, not in a header.
    // -----------------------------------------------------------------------

    /// <summary>
    /// 50 concurrent Wave payout calls all reach upstream, and every outbound body
    /// carries the same <c>idempotency_key</c> token. Asserting on the body is the
    /// strongest invariant we can test client-side; Wave's server-side dedup is the
    /// last line of defence.
    /// </summary>
    [Fact]
    public async Task WavePayout_FiftyConcurrentCalls_SameKey_EveryBodyCarriesSameIdempotencyKey()
    {
        var observedKeys = new List<string?>();
        var keyLock = new object();
        var handler = new CountingStubHttpMessageHandler((req, _) =>
        {
            if (req.Content is not null)
            {
                var body = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                lock (keyLock) observedKeys.Add(ExtractJsonStringField(body, "idempotency_key"));
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po-wave-concurrent","status":"processing","receive_amount":"2500","currency":"XOF"}
                """);
        });

        var provider = new WavePaymentProvider(
            new HttpClient(handler),
            Options.Create(new WaveOptions
            {
                ApiKey = "wave_sn_prod_xxx",
                WebhookSecret = "webhook-test-secret",
                Currency = "XOF",
                SuccessUrl = "https://example.com/success",
                ErrorUrl = "https://example.com/error"
            }),
            NullLogger<WavePaymentProvider>.Instance);

        var request = new PayoutRequest
        {
            DestinationToken = "SN:221761234567",
            Amount = 2500m,
            Currency = "XOF",
            Description = "wave-concurrent-payout",
            IdempotencyKey = "wave-concurrent-payout-key"
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.ProcessPayoutAsync(request)));

        Assert.Equal(ConcurrentCallers, handler.CallCount);
        Assert.Equal(ConcurrentCallers, observedKeys.Count);
        Assert.All(observedKeys, k => Assert.Equal("wave-concurrent-payout-key", k));
        Assert.All(results, r => Assert.Equal("po-wave-concurrent", r.GatewayReference));
    }

    /// <summary>
    /// Helper: extract <c>"name":"value"</c> from a JSON body without taking a
    /// JsonDocument allocation per request inside the test handler (which is
    /// invoked 50× concurrently).
    /// </summary>
    private static string? ExtractJsonStringField(string body, string fieldName)
    {
        var needle = $"\"{fieldName}\":\"";
        var start = body.IndexOf(needle, StringComparison.Ordinal);
        if (start < 0) return null;
        start += needle.Length;
        var end = body.IndexOf('"', start);
        return end < 0 ? null : body[start..end];
    }

    // -----------------------------------------------------------------------
    //  Flutterwave — internal FlutterwaveIdempotencyCache. The provider creates
    //  its own cache instance per construction, so we test through the public
    //  ProcessPaymentAsync surface and assert the in-flight coalesce collapses
    //  50 callers onto a single upstream HTTP call.
    // -----------------------------------------------------------------------

    /// <summary>
    /// 50 concurrent <see cref="FlutterwavePaymentProvider.ProcessPaymentAsync"/> calls under the
    /// same <see cref="PaymentRequest.IdempotencyKey"/> coalesce onto a single upstream POST to
    /// <c>v3/payments</c>. All 50 callers observe the same cached <see cref="PaymentResponse"/>.
    /// </summary>
    [Fact]
    public async Task Flutterwave_FiftyConcurrentCalls_SameKey_DedupesToOneHttpCall()
    {
        var handler = new CountingStubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Hosted Link","data":{"link":"https://checkout.flutterwave.com/v3/hosted/pay/concurrent"}}
                """));

        var provider = new FlutterwavePaymentProvider(
            new HttpClient(handler),
            Options.Create(new FlutterwaveOptions
            {
                SecretKey = "FLWSECK_TEST-xxx",
                PublicKey = "FLWPUBK_TEST-xxx",
                EncryptionKey = "FLWSECK_TEST_xxx",
                WebhookSecret = "verify",
                RedirectUrl = "https://example.com/return"
            }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

        var request = new PaymentRequest
        {
            PaymentMethodToken = "tx-concurrent-1",
            Amount = 100m,
            Currency = "NGN",
            Description = "fw-concurrent",
            IdempotencyKey = "fw-concurrent-key-1",
            Metadata = new Dictionary<string, string>
            {
                ["email"] = "buyer@example.com",
                ["name"] = "Buyer Concurrent"
            }
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.ProcessPaymentAsync(request)));

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(ConcurrentCallers, results.Length);
        Assert.All(results, r => Assert.Equal("tx-concurrent-1", r.GatewayReference));
        // Coalesced callers observe the same materialised PaymentResponse — the cache hands
        // back the same instance to every caller waiting on the in-flight task.
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    // -----------------------------------------------------------------------
    //  PayFast — PayFastPlanCache. CreatePlanAsync writes a new entry per call
    //  (each call generates a new plan reference), so the natural concurrency
    //  contract here is that 50 concurrent CreatePlanAsync calls all succeed
    //  without data races, returning 50 distinct references. NO HTTP call is
    //  made (plan creation is local-cache only on PayFast), so the assertion
    //  is on the cache state, not on upstream call count.
    //  Plan FETCH after creation must always return the same reference.
    // -----------------------------------------------------------------------

    /// <summary>
    /// 50 concurrent <see cref="PayFastSubscriptionProvider.CreatePlanAsync"/> calls each create a
    /// fresh plan in <see cref="PayFastPlanCache"/>. Asserts (a) no exception escapes, (b) all 50
    /// plans were durably written and individually retrievable, and (c) no upstream HTTP call was
    /// made — plan creation is local-cache-only on PayFast (no plan resource on the wire).
    /// </summary>
    [Fact]
    public async Task PayFast_FiftyConcurrentCreatePlanCalls_AllSucceedWithoutHttpAndAllReadable()
    {
        var handler = new CountingStubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));

        var cache = new PayFastPlanCache();
        var provider = new PayFastSubscriptionProvider(
            new HttpClient(handler),
            Options.Create(new PayFastOptions
            {
                MerchantId = "10000100",
                MerchantKey = "46f0cd694581a",
                Passphrase = "jt7NOE43FZPn",
                UseSandbox = true,
                ReturnUrl = "https://example.com/return",
                CancelUrl = "https://example.com/cancel",
                NotifyUrl = "https://example.com/notify"
            }),
            NullLogger<PayFastSubscriptionProvider>.Instance,
            cache);

        var planRequest = new PlanRequest
        {
            Name = "Concurrent Plan",
            Amount = 50m,
            Currency = "ZAR",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 0
        };

        // 50 parallel CreatePlanAsync — each generates its OWN plan reference.
        var plans = await Task.WhenAll(
            Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.CreatePlanAsync(planRequest)));

        Assert.Equal(ConcurrentCallers, plans.Length);
        // No upstream HTTP — plan creation is local-cache only on PayFast.
        Assert.Equal(0, handler.CallCount);
        // Every plan reference is distinct (no key collisions under concurrency).
        var references = plans.Select(p => p.Reference).ToArray();
        Assert.Equal(ConcurrentCallers, references.Distinct(StringComparer.Ordinal).Count());

        // Every cached plan must round-trip — the cache survived all 50 concurrent writes.
        foreach (var p in plans)
        {
            var fetched = await provider.GetPlanAsync(p.Reference);
            Assert.NotNull(fetched);
            Assert.Equal(p.Reference, fetched!.Reference);
            Assert.Equal(planRequest.Amount, fetched.Amount);
        }
    }

    /// <summary>
    /// Second variant: 50 concurrent <see cref="PayFastSubscriptionProvider.GetPlanAsync"/> reads
    /// of the SAME previously-written plan reference all succeed and return structurally-equal
    /// <see cref="Plan"/> records. Demonstrates the read path under contention is also safe.
    /// </summary>
    [Fact]
    public async Task PayFast_FiftyConcurrentGetPlanCalls_SameReference_AllReturnSamePlan()
    {
        var cache = new PayFastPlanCache();
        var provider = new PayFastSubscriptionProvider(
            new HttpClient(new CountingStubHttpMessageHandler((_, _) =>
                StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"))),
            Options.Create(new PayFastOptions
            {
                MerchantId = "10000100",
                MerchantKey = "46f0cd694581a",
                Passphrase = "jt7NOE43FZPn",
                UseSandbox = true
            }),
            NullLogger<PayFastSubscriptionProvider>.Instance,
            cache);

        var seed = await provider.CreatePlanAsync(new PlanRequest
        {
            Name = "Shared",
            Amount = 199m,
            Currency = "ZAR",
            Interval = SubscriptionInterval.Monthly,
            TotalCycles = 12
        });

        var fetches = await Task.WhenAll(
            Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.GetPlanAsync(seed.Reference)));

        Assert.Equal(ConcurrentCallers, fetches.Length);
        Assert.All(fetches, p =>
        {
            Assert.NotNull(p);
            Assert.Equal(seed.Reference, p!.Reference);
            Assert.Equal(199m, p.Amount);
        });
    }

    // -----------------------------------------------------------------------
    //  Stripe (second wave) — extends the existing single-test coverage with
    //  a Refund-flow variant. Stripe.net regenerates the auto-idempotency
    //  header on EACH PaymentIntentService.CreateAsync, so the only way for
    //  the SDK to share a key across 50 concurrent retries is when the caller
    //  supplies one via Stripe.RequestOptions.IdempotencyKey (= our
    //  RefundRequest.IdempotencyKey). Asserts the same key is on every wire.
    // -----------------------------------------------------------------------

    /// <summary>
    /// 50 concurrent <see cref="StripePaymentProvider.ProcessRefundAsync"/> calls under the same
    /// <see cref="RefundRequest.IdempotencyKey"/>: every outbound request carries the same
    /// <c>Idempotency-Key</c> header so Stripe's server-side dedup collapses them to a single
    /// refund. The Stripe.net SDK does NOT coalesce client-side, so we expect 50 HTTP calls.
    /// </summary>
    [Fact]
    public async Task StripeRefund_FiftyConcurrentCalls_SameKey_EveryRequestCarriesSameIdempotencyHeader()
    {
        var observedKeys = new List<string?>();
        var keyLock = new object();
        var handler = new CountingStubHttpMessageHandler((req, _) =>
        {
            var headerValue = req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;
            lock (keyLock) observedKeys.Add(headerValue);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"re_stripe_concurrent","object":"refund","amount":500,"currency":"usd","status":"succeeded","payment_intent":"pi_x"}
                """);
        });

        var provider = new StripePaymentProvider(
            new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripePaymentProvider>.Instance);

        var request = new RefundRequest
        {
            GatewayReference = "pi_x",
            Amount = 5m,
            Reason = "stripe-refund-concurrent",
            IdempotencyKey = "stripe-refund-concurrent-key"
        };

        var results = await Task.WhenAll(
            Enumerable.Range(0, ConcurrentCallers).Select(_ => provider.ProcessRefundAsync(request)));

        Assert.Equal(ConcurrentCallers, handler.CallCount);
        Assert.Equal(ConcurrentCallers, observedKeys.Count);
        Assert.All(observedKeys, k => Assert.Equal("stripe-refund-concurrent-key", k));
        Assert.All(results, r => Assert.Equal("re_stripe_concurrent", r.GatewayReference));
    }

    // -----------------------------------------------------------------------
    //  Note on Yoco: the YocoPaymentProvider has NO idempotency cache and the
    //  Yoco Online REST API does not accept an Idempotency-Key header. The
    //  YocoTokenCache exists for tokenisation reads (PaymentMethod lookups),
    //  NOT for ProcessPaymentAsync or CheckoutAsync de-duplication. A concurrent
    //  "50→1" dedup test cannot exist for Yoco within its current contract;
    //  callers must supply their own at-most-once retry strategy at the HTTP
    //  layer. See depth report — Yoco intentionally skipped from this category.
    // -----------------------------------------------------------------------
}
