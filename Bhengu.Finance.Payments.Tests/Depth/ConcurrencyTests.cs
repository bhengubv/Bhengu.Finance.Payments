// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
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
}
