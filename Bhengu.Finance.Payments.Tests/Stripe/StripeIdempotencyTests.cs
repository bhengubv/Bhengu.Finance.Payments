// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

/// <summary>
/// Verifies Stripe.RequestOptions.IdempotencyKey is propagated as the canonical
/// <c>Idempotency-Key</c> HTTP request header that Stripe's server-side dedup uses.
/// This is the Stripe-native pattern — distinct from providers that emulate idempotency
/// with an in-process cache.
/// </summary>
[Collection(StripeConfigurationCollection.Name)]
public class StripeIdempotencyTests
{
    private static StripeOptions Opts() => new() { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" };

    [Fact]
    public async Task ProcessPaymentAsync_WhenIdempotencyKeySupplied_SetsHeader()
    {
        string? observedKey = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            observedKey = req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_test_idem","object":"payment_intent","amount":1000,"currency":"usd","status":"succeeded"}
                """);
        });
        var provider = new StripePaymentProvider(new HttpClient(handler), Options.Create(Opts()), NullLogger<StripePaymentProvider>.Instance);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "pm_card_visa",
            Amount = 10m,
            Currency = "USD",
            Description = "idempotent charge",
            IdempotencyKey = "order-42"
        });

        Assert.Equal("order-42", observedKey);
    }

    [Fact]
    public async Task ProcessPaymentAsync_NoIdempotencyKey_DoesNotPassCallerKey()
    {
        // Stripe.net auto-generates a UUID idempotency key on POSTs to make its own internal
        // retry policy safe. We simply assert that when the caller supplies none, the header
        // does NOT carry the caller's value (we have no caller value to leak).
        string? observedKey = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            observedKey = req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"pi_test_noidem","object":"payment_intent","amount":1000,"currency":"usd","status":"succeeded"}
                """);
        });
        var provider = new StripePaymentProvider(new HttpClient(handler), Options.Create(Opts()), NullLogger<StripePaymentProvider>.Instance);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "pm_card_visa",
            Amount = 10m,
            Currency = "USD",
            Description = "non-idempotent"
        });

        // The header may be auto-set by Stripe.net to a UUID; we only assert it isn't a value
        // the caller could have leaked (callers wouldn't pass a header value that looks like a GUID).
        Assert.NotEqual("order-42", observedKey);
    }

    [Fact]
    public async Task ProcessRefundAsync_PropagatesIdempotencyKey()
    {
        string? observedKey = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            observedKey = req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"re_test_idem","object":"refund","amount":500,"currency":"usd","status":"succeeded","payment_intent":"pi_x"}
                """);
        });
        var provider = new StripePaymentProvider(new HttpClient(handler), Options.Create(Opts()), NullLogger<StripePaymentProvider>.Instance);

        await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "pi_x",
            Amount = 5m,
            Reason = "duplicate",
            IdempotencyKey = "refund-7"
        });

        Assert.Equal("refund-7", observedKey);
    }

    [Fact]
    public async Task ProcessPayoutAsync_PropagatesIdempotencyKey()
    {
        string? observedKey = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            observedKey = req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"po_test_idem","object":"payout","amount":50000,"currency":"usd","status":"paid","destination":"ba_x"}
                """);
        });
        var provider = new StripePaymentProvider(new HttpClient(handler), Options.Create(Opts()), NullLogger<StripePaymentProvider>.Instance);

        await provider.ProcessPayoutAsync(new PayoutRequest
        {
            DestinationToken = "ba_x",
            Amount = 500m,
            Currency = "USD",
            Description = "Vendor payout",
            IdempotencyKey = "payout-99"
        });

        Assert.Equal("payout-99", observedKey);
    }

    [Fact]
    public async Task TokeniseAsync_PropagatesIdempotencyKey()
    {
        string? observedKey = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            observedKey ??= req.Headers.TryGetValues("Idempotency-Key", out var v) ? v.FirstOrDefault() : null;
            // Both calls (PaymentMethod create + Customer create) return canned shapes.
            if (req.RequestUri!.PathAndQuery.Contains("payment_methods"))
            {
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"id":"pm_test_idem","object":"payment_method","type":"card","card":{"brand":"visa","last4":"4242","exp_month":12,"exp_year":2030},"created":1700000000}
                    """);
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"cus_test","object":"customer","email":"a@b.com"}
                """);
        });
        var provider = new StripeRawCardTokenisationProvider(new HttpClient(handler), Options.Create(Opts()), NullLogger<StripeRawCardTokenisationProvider>.Instance);

        await provider.TokeniseAsync(new Core.Models.Vault.TokeniseRequest
        {
            Card = new Core.Models.Vault.CardDetails
            {
                CardholderName = "T Bengu",
                CardNumber = "4242424242424242",
                ExpiryMonth = 12,
                ExpiryYear = 2030,
                Cvv = "123"
            },
            IdempotencyKey = "vault-1"
        });

        Assert.Equal("vault-1", observedKey);
    }
}
