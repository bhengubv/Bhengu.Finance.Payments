// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Internals;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

/// <summary>
/// Exercises the in-memory idempotency dedupe wired into Flutterwave's mutation endpoints.
/// Flutterwave's REST API has no native Idempotency-Key header, so the SDK coalesces concurrent
/// calls sharing the same <c>IdempotencyKey</c> onto a single in-flight task.
/// </summary>
public class FlutterwaveIdempotencyTests
{
    [Fact]
    public async Task IdempotencyCache_NullKey_InvokesFactoryEveryTime()
    {
        var cache = new FlutterwaveIdempotencyCache();
        var calls = 0;
        var r1 = await cache.GetOrAddAsync<Boxed>(null, () => { calls++; return Task.FromResult(new Boxed(42)); });
        var r2 = await cache.GetOrAddAsync<Boxed>(null, () => { calls++; return Task.FromResult(new Boxed(42)); });
        Assert.Equal(42, r1.Value);
        Assert.Equal(42, r2.Value);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task IdempotencyCache_SameKey_InvokesFactoryOnce()
    {
        var cache = new FlutterwaveIdempotencyCache();
        var calls = 0;
        var r1 = await cache.GetOrAddAsync("k1", () => { calls++; return Task.FromResult(new Boxed(99)); });
        var r2 = await cache.GetOrAddAsync("k1", () => { calls++; return Task.FromResult(new Boxed(0)); });
        Assert.Equal(99, r1.Value);
        Assert.Equal(99, r2.Value);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IdempotencyCache_DifferentKeys_InvokesFactoryPerKey()
    {
        var cache = new FlutterwaveIdempotencyCache();
        var calls = 0;
        await cache.GetOrAddAsync("k1", () => { calls++; return Task.FromResult(new Boxed(1)); });
        await cache.GetOrAddAsync("k2", () => { calls++; return Task.FromResult(new Boxed(2)); });
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task IdempotencyCache_FaultedTask_IsEvicted_AndRetried()
    {
        var cache = new FlutterwaveIdempotencyCache();
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrAddAsync<Boxed>("k", () => { calls++; throw new InvalidOperationException("boom"); }));

        // Give the eviction continuation a tick to run.
        await Task.Delay(50);

        var second = await cache.GetOrAddAsync<Boxed>("k", () => { calls++; return Task.FromResult(new Boxed(7)); });
        Assert.Equal(7, second.Value);
        Assert.Equal(2, calls);
    }

    private sealed record Boxed(int Value);

    [Fact]
    public async Task ProcessPaymentAsync_WithIdempotencyKey_DeduplicatesIdenticalRetries()
    {
        var httpCalls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            httpCalls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","message":"Hosted","data":{"link":"https://checkout.flutterwave.com/x"}}
                """);
        });
        var provider = new FlutterwavePaymentProvider(
            new HttpClient(handler),
            Options.Create(new FlutterwaveOptions { SecretKey = "FLWSECK_TEST" }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

        var req = new PaymentRequest
        {
            PaymentMethodToken = "tx-1",
            Amount = 50m,
            Currency = "NGN",
            Description = "Dedup test",
            IdempotencyKey = "order-7",
            Metadata = new Dictionary<string, string> { ["email"] = "buyer@example.com" }
        };

        var r1 = await provider.ProcessPaymentAsync(req);
        var r2 = await provider.ProcessPaymentAsync(req);

        Assert.Equal(1, httpCalls);
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
    }

    [Fact]
    public async Task ProcessPaymentAsync_WithoutIdempotencyKey_HitsEndpointEveryCall()
    {
        var httpCalls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            httpCalls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"link":"https://x"}}
                """);
        });
        var provider = new FlutterwavePaymentProvider(
            new HttpClient(handler),
            Options.Create(new FlutterwaveOptions { SecretKey = "FLWSECK_TEST" }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

        var req = new PaymentRequest
        {
            PaymentMethodToken = "tx-1",
            Amount = 50m,
            Currency = "NGN",
            Description = "No dedup",
            Metadata = new Dictionary<string, string> { ["email"] = "buyer@example.com" }
        };

        await provider.ProcessPaymentAsync(req);
        await provider.ProcessPaymentAsync(req);

        Assert.Equal(2, httpCalls);
    }

    [Fact]
    public async Task ProcessPayoutAsync_WithIdempotencyKey_Deduplicates()
    {
        var httpCalls = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            httpCalls++;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":1,"reference":"transfer-1","status":"NEW","amount":500}}
                """);
        });
        var provider = new FlutterwavePaymentProvider(
            new HttpClient(handler),
            Options.Create(new FlutterwaveOptions { SecretKey = "FLWSECK_TEST" }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

        var req = new PayoutRequest
        {
            DestinationToken = "044:0690000040",
            Amount = 500m,
            Currency = "NGN",
            Description = "Vendor",
            IdempotencyKey = "payout-7"
        };

        await provider.ProcessPayoutAsync(req);
        await provider.ProcessPayoutAsync(req);

        Assert.Equal(1, httpCalls);
    }
}
