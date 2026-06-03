// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackIdempotencyTests
{
    [Fact]
    public async Task PaymentProvider_SameIdempotencyKey_DedupesToSingleHttpCall()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"message":"ok","data":{"id":1,"reference":"ref_only_once","status":"success","amount":10000,"currency":"NGN"}}
                """);
        });

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
            Description = "idempotent",
            IdempotencyKey = "key-1234"
        };

        var first = await provider.ProcessPaymentAsync(request);
        var second = await provider.ProcessPaymentAsync(request);

        Assert.Equal(1, callCount);
        Assert.Equal(first.GatewayReference, second.GatewayReference);
    }

    [Fact]
    public async Task PaymentProvider_DifferentKey_DoesNotDedupe()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            var n = Interlocked.Increment(ref callCount);
            var body = "{\"status\":true,\"message\":\"ok\",\"data\":{\"id\":" + n + ",\"reference\":\"ref_" + n + "\",\"status\":\"success\",\"amount\":10000,\"currency\":\"NGN\"}}";
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, body);
        });

        var cache = new PaystackIdempotencyCache();
        var provider = new PaystackPaymentProvider(
            new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance,
            cache);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "AUTH_x", Amount = 100m, Currency = "NGN", Description = "a", IdempotencyKey = "k-a"
        });
        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "AUTH_x", Amount = 100m, Currency = "NGN", Description = "b", IdempotencyKey = "k-b"
        });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task PaymentProvider_NoKey_IssuesEveryRequest()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"reference":"r","status":"success","amount":10,"currency":"NGN"}}
                """);
        });

        var provider = new PaystackPaymentProvider(
            new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance,
            new PaystackIdempotencyCache());

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "x", Amount = 1m, Currency = "NGN", Description = "noKey"
        });
        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "x", Amount = 1m, Currency = "NGN", Description = "noKey"
        });

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RefundProvider_SameIdempotencyKey_DedupesToSingleHttpCall()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"transaction_id":1,"refund_reference":"rf_1","amount":1000,"status":"processed"}}
                """);
        });
        var cache = new PaystackIdempotencyCache();
        var provider = new PaystackPaymentProvider(
            new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk" }),
            NullLogger<PaystackPaymentProvider>.Instance,
            cache);
        var request = new RefundRequest
        {
            GatewayReference = "ref",
            Amount = 10m,
            Reason = "test",
            IdempotencyKey = "rf-key"
        };
        await provider.ProcessRefundAsync(request);
        await provider.ProcessRefundAsync(request);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task PayoutProvider_SameKey_DedupesToSingleHttpCall()
    {
        var callCount = 0;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"reference":"t1","status":"success"}}
                """);
        });
        var cache = new PaystackIdempotencyCache();
        var provider = new PaystackPayoutProvider(
            new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk" }),
            NullLogger<PaystackPayoutProvider>.Instance,
            cache);
        var request = new PayoutRequest
        {
            DestinationToken = "RCP_x", Amount = 50m, Currency = "NGN", Description = "payout",
            IdempotencyKey = "payout-key-1"
        };
        await provider.ProcessPayoutAsync(request);
        await provider.ProcessPayoutAsync(request);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Invalidate_RemovesCachedEntry()
    {
        var cache = new PaystackIdempotencyCache();
        // Seed an entry.
        var key = "to-invalidate";
        _ = cache.GetOrAddAsync(key, () => Task.FromResult("first"));
        Assert.True(cache.Invalidate(key));
        Assert.False(cache.Invalidate(key));
    }

    [Fact]
    public async Task IdempotencyCache_NullKey_DoesNotCache()
    {
        var cache = new PaystackIdempotencyCache();
        var calls = 0;
        async Task<int> Factory()
        {
            await Task.Yield();
            return Interlocked.Increment(ref calls);
        }
        var first = await cache.GetOrAddAsync<int>(null, Factory);
        var second = await cache.GetOrAddAsync<int>(null, Factory);
        Assert.NotEqual(first, second);
        Assert.Equal(2, calls);
    }
}
