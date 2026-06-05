// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http;
using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Providers;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Bhengu.Finance.Payments.Kashier.Providers;
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Providers;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Bhengu.Finance.Payments.MTNMoMo.Providers;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Bhengu.Finance.Payments.Stitch.Providers;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.Stripe;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.TymeBank.Configuration;
using Bhengu.Finance.Payments.TymeBank.Providers;
using Bhengu.Finance.Payments.Wave.Configuration;
using Bhengu.Finance.Payments.Wave.Providers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Depth;

/// <summary>
/// HttpMessageHandler that never produces a response until cancellation fires. Used to test
/// the in-flight cancellation path — caller fires the operation, sees the call sit on the wire,
/// then cancels the token. Provider must propagate <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class NeverRespondingHandler : HttpMessageHandler
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);
        // Suspends until cancellation — Task.Delay throws TaskCanceledException on ct fire.
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        // Unreachable, but the compiler can't see that.
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>
/// HttpMessageHandler that throws if SendAsync ever runs. Used to test the pre-cancelled path —
/// the operation must throw before any HTTP layer activity.
/// </summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    private int _callCount;
    public int CallCount => Volatile.Read(ref _callCount);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref _callCount);
        throw new InvalidOperationException("Handler must not be called when token is pre-cancelled.");
    }
}

/// <summary>
/// Routing handler for OAuth-fronted providers. The first call (OAuth/token endpoint) completes
/// quickly with a canned token response; ALL subsequent calls are blocked indefinitely until
/// cancellation. Lets us test the "cancel between OAuth and business call" race.
/// </summary>
internal sealed class OAuthThenBlockHandler : HttpMessageHandler
{
    private readonly Predicate<Uri> _isAuth;
    private readonly string _tokenJson;
    private int _authCount;
    private int _businessCount;
    public int AuthCount => Volatile.Read(ref _authCount);
    public int BusinessCount => Volatile.Read(ref _businessCount);
    public ManualResetEventSlim AuthCalled { get; } = new(false);

    public OAuthThenBlockHandler(Predicate<Uri> isAuth, string tokenJson)
    {
        _isAuth = isAuth ?? throw new ArgumentNullException(nameof(isAuth));
        _tokenJson = tokenJson ?? throw new ArgumentNullException(nameof(tokenJson));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_isAuth(request.RequestUri!))
        {
            Interlocked.Increment(ref _authCount);
            AuthCalled.Set();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, _tokenJson);
        }
        Interlocked.Increment(ref _businessCount);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>
/// Cancellation-propagation tests across every HTTP-bound provider. Three scenarios per provider:
/// <list type="number">
///   <item><c>PreCancelled</c> — token cancelled before call → throws <see cref="OperationCanceledException"/>
///     and the handler is never invoked.</item>
///   <item><c>MidFlight</c> — handler hangs forever; we cancel after 100 ms; provider must propagate
///     <see cref="OperationCanceledException"/>.</item>
///   <item><c>BetweenOAuthAndBusiness</c> — for providers that fetch an OAuth token before the business
///     call, cancel after the OAuth call returns but before the business call completes. Asserts
///     OperationCanceledException + business endpoint may or may not be touched (race) but if touched
///     the operation still cancels.</item>
/// </list>
/// </summary>
public sealed class CancellationTests
{
    private static PaymentRequest SimplePayment(string token = "tok", string currency = "USD") => new()
    {
        PaymentMethodToken = token,
        Amount = 100m,
        Currency = currency,
        Description = "cancel-test",
        Metadata = new Dictionary<string, string>
        {
            ["email"] = "buyer@example.com",
            ["name"] = "Buyer Cancel"
        }
    };

    // -------------------------------------------------------------------
    //  Paystack
    // -------------------------------------------------------------------
    private static PaystackPaymentProvider PaystackProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com" }),
            NullLogger<PaystackPaymentProvider>.Instance,
            new PaystackIdempotencyCache());

    [Fact]
    public async Task Paystack_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = PaystackProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("AUTH_x", "NGN"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Paystack_ProcessPaymentAsync_MidFlightCancellation_Propagates()
    {
        var handler = new NeverRespondingHandler();
        var provider = PaystackProvider(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("AUTH_y", "NGN"), cts.Token));
    }

    // -------------------------------------------------------------------
    //  Stripe
    // -------------------------------------------------------------------
    [Collection(StripeConfigurationCollection.Name)]
    public sealed class Stripe_CancellationTests
    {
        private static StripePaymentProvider Create(HttpMessageHandler handler) =>
            new(new HttpClient(handler),
                Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
                NullLogger<StripePaymentProvider>.Instance);

        [Fact]
        public async Task Stripe_ProcessPaymentAsync_PreCancelled_ThrowsOperationCanceled()
        {
            var handler = new ThrowingHandler();
            var provider = Create(handler);
            var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.ProcessPaymentAsync(new PaymentRequest
                {
                    PaymentMethodToken = "pm_card_visa",
                    Amount = 10m, Currency = "USD", Description = "stripe-cancel"
                }, cts.Token));
            // Stripe.net may inspect the token at the boundary too — either 0 OR handler-throws-then-wraps is OK.
        }

        [Fact]
        public async Task Stripe_ProcessPaymentAsync_MidFlightCancellation_Propagates()
        {
            var handler = new NeverRespondingHandler();
            var provider = Create(handler);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                provider.ProcessPaymentAsync(new PaymentRequest
                {
                    PaymentMethodToken = "pm_card_visa",
                    Amount = 10m, Currency = "USD", Description = "stripe-cancel-mid"
                }, cts.Token));
        }
    }

    // -------------------------------------------------------------------
    //  Flutterwave
    // -------------------------------------------------------------------
    private static FlutterwavePaymentProvider FlutterwaveProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new FlutterwaveOptions
            {
                SecretKey = "FLWSECK_TEST-xxx", PublicKey = "p", EncryptionKey = "e",
                WebhookSecret = "wh", RedirectUrl = "https://example.com/r"
            }),
            NullLogger<FlutterwavePaymentProvider>.Instance);

    [Fact]
    public async Task Flutterwave_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = FlutterwaveProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tx-cancel-1", "NGN"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Flutterwave_ProcessPaymentAsync_MidFlightCancellation_Propagates()
    {
        var handler = new NeverRespondingHandler();
        var provider = FlutterwaveProvider(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tx-cancel-2", "NGN"), cts.Token));
    }

    // -------------------------------------------------------------------
    //  Yoco
    // -------------------------------------------------------------------
    private static YocoPaymentProvider YocoProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new YocoOptions { SecretKey = "sk", WebhookSecret = "wh" }),
            NullLogger<YocoPaymentProvider>.Instance);

    [Fact]
    public async Task Yoco_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = YocoProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tok_yoco", "ZAR"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Yoco_ProcessPaymentAsync_MidFlightCancellation_Propagates()
    {
        var handler = new NeverRespondingHandler();
        var provider = YocoProvider(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tok_yoco_mid", "ZAR"), cts.Token));
    }

    // -------------------------------------------------------------------
    //  Wave
    // -------------------------------------------------------------------
    private static WavePaymentProvider WaveProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new WaveOptions
            {
                ApiKey = "wave_sn_prod_xxx", WebhookSecret = "wh", Currency = "XOF",
                SuccessUrl = "https://example.com/s", ErrorUrl = "https://example.com/e"
            }),
            NullLogger<WavePaymentProvider>.Instance);

    [Fact]
    public async Task Wave_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = WaveProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("wave-ref", "XOF"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Wave_ProcessPaymentAsync_MidFlightCancellation_Propagates()
    {
        var handler = new NeverRespondingHandler();
        var provider = WaveProvider(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("wave-ref-mid", "XOF"), cts.Token));
    }

    // -------------------------------------------------------------------
    //  Paymob (no OAuth in ProcessPayment but has its own auth handshake)
    // -------------------------------------------------------------------
    private static PaymobPaymentProvider PaymobProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaymobOptions
            {
                ApiKey = "api", HmacSecret = "hmac", IntegrationId = 1, IframeId = 1, Currency = "EGP"
            }),
            NullLogger<PaymobPaymentProvider>.Instance,
            new PaymobIdempotencyCache(new InMemoryBhenguDistributedCache()));

    [Fact]
    public async Task Paymob_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = PaymobProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tok_paymob", "EGP"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Paymob_ProcessPaymentAsync_MidFlightCancellation_Propagates()
    {
        var handler = new NeverRespondingHandler();
        var provider = PaymobProvider(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tok_paymob_mid", "EGP"), cts.Token));
    }

    [Fact]
    public async Task Paymob_CancelAfterOAuthFetch_PropagatesOperationCanceled()
    {
        var handler = new OAuthThenBlockHandler(
            u => u.PathAndQuery.Contains("api/auth/tokens", StringComparison.Ordinal),
            "{\"token\":\"auth_token_abc\"}");
        var provider = PaymobProvider(handler);

        using var cts = new CancellationTokenSource();
        var payTask = Task.Run(() => provider.ProcessPaymentAsync(SimplePayment("tok_paymob_oauth", "EGP"), cts.Token));
        Assert.True(handler.AuthCalled.Wait(TimeSpan.FromSeconds(5)), "OAuth call did not complete in time");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payTask);
        Assert.Equal(1, handler.AuthCount);
    }

    // -------------------------------------------------------------------
    //  Kashier (no OAuth, has idempotency cache)
    // -------------------------------------------------------------------
    private static KashierPaymentProvider KashierProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new KashierOptions
            {
                ApiKey = "api_test_key", MerchantId = "MID_1", SecretKey = "secret",
                WebhookSecret = "wh", Currency = "EGP", Mode = "test", UseSandbox = true
            }),
            NullLogger<KashierPaymentProvider>.Instance,
            new KashierIdempotencyCache(new InMemoryBhenguDistributedCache()));

    [Fact]
    public async Task Kashier_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = KashierProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tok_kashier", "EGP"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Kashier_ProcessPaymentAsync_MidFlightCancellation_Propagates()
    {
        var handler = new NeverRespondingHandler();
        var provider = KashierProvider(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("tok_kashier_mid", "EGP"), cts.Token));
    }

    // -------------------------------------------------------------------
    //  Stitch — OAuth provider
    // -------------------------------------------------------------------
    private static StitchPaymentProvider StitchProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new StitchOptions
            {
                ClientId = "c", ApiKey = "k", WebhookSecret = "wh",
                BeneficiaryAccountNumber = "1", BeneficiaryBankId = "fnb",
                BeneficiaryName = "n", Currency = "ZAR"
            }),
            NullLogger<StitchPaymentProvider>.Instance);

    [Fact]
    public async Task Stitch_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = StitchProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("stitch-ref", "ZAR"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Stitch_ProcessPaymentAsync_MidFlightCancellation_Propagates()
    {
        var handler = new NeverRespondingHandler();
        var provider = StitchProvider(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("stitch-mid", "ZAR"), cts.Token));
    }

    // -------------------------------------------------------------------
    //  TymeBank — OAuth provider with documented oauth2/token endpoint
    // -------------------------------------------------------------------
    private static TymeBankPaymentProvider TymeBankProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new TymeBankOptions
            {
                ClientId = "c", ClientSecret = "s", MerchantId = "M",
                WebhookSecret = "wh", Currency = "ZAR", CallbackUrl = "https://example.com/cb"
            }),
            NullLogger<TymeBankPaymentProvider>.Instance);

    [Fact]
    public async Task TymeBank_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = TymeBankProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(new PaymentRequest
            {
                PaymentMethodToken = "pay-ref-1",
                Amount = 100m,
                Currency = "ZAR",
                Description = "tyme-cancel",
                Metadata = new Dictionary<string, string>
                {
                    ["debtor_account"] = "1234567890",
                    ["debtor_branch_code"] = "678910",
                    ["creditor_account"] = "0987654321",
                    ["creditor_branch_code"] = "123456",
                    ["creditor_name"] = "M"
                }
            }, cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task TymeBank_CancelAfterOAuthFetch_PropagatesOperationCanceled()
    {
        var handler = new OAuthThenBlockHandler(
            u => u.PathAndQuery.Contains("oauth2/token", StringComparison.OrdinalIgnoreCase),
            "{\"access_token\":\"tyme-tok-123\",\"token_type\":\"Bearer\",\"expires_in\":3600}");
        var provider = TymeBankProvider(handler);

        using var cts = new CancellationTokenSource();
        var payTask = Task.Run(() => provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "pay-ref-oauth",
            Amount = 100m, Currency = "ZAR", Description = "tyme-cancel-oauth",
            Metadata = new Dictionary<string, string>
            {
                ["debtor_account"] = "1234567890",
                ["debtor_branch_code"] = "678910",
                ["creditor_account"] = "0987654321",
                ["creditor_branch_code"] = "123456",
                ["creditor_name"] = "M"
            }
        }, cts.Token));
        Assert.True(handler.AuthCalled.Wait(TimeSpan.FromSeconds(5)), "OAuth did not complete in time");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payTask);
        Assert.Equal(1, handler.AuthCount);
    }

    // -------------------------------------------------------------------
    //  MPesa — OAuth provider
    // -------------------------------------------------------------------
    private static MPesaPaymentProvider MPesaProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
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

    [Fact]
    public async Task MPesa_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = MPesaProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("254712345678", "KES"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task MPesa_CancelAfterOAuthFetch_PropagatesOperationCanceled()
    {
        var handler = new OAuthThenBlockHandler(
            u => u.PathAndQuery.Contains("oauth/v1/generate", StringComparison.Ordinal),
            "{\"access_token\":\"mpesa-tok\",\"expires_in\":\"3599\"}");
        var provider = MPesaProvider(handler);

        using var cts = new CancellationTokenSource();
        var payTask = Task.Run(() => provider.ProcessPaymentAsync(SimplePayment("254712345678", "KES"), cts.Token));
        Assert.True(handler.AuthCalled.Wait(TimeSpan.FromSeconds(5)), "OAuth did not complete in time");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payTask);
        Assert.Equal(1, handler.AuthCount);
    }

    // -------------------------------------------------------------------
    //  MTN MoMo — OAuth provider
    // -------------------------------------------------------------------
    private static MTNMoMoPaymentProvider MTNMoMoProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new MTNMoMoOptions
            {
                SubscriptionKey = "sub-key", ApiUserId = "00000000-0000-0000-0000-000000000001",
                ApiKey = "api-key-secret", TargetEnvironment = "sandbox",
                CallbackUrl = "https://example.com/momo/cb", UseSandbox = true
            }),
            NullLogger<MTNMoMoPaymentProvider>.Instance);

    [Fact]
    public async Task MTNMoMo_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = MTNMoMoProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("256771234567", "EUR"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task MTNMoMo_CancelAfterOAuthFetch_PropagatesOperationCanceled()
    {
        var handler = new OAuthThenBlockHandler(
            u => u.PathAndQuery.EndsWith("/token/", StringComparison.Ordinal),
            "{\"access_token\":\"momo-tok\",\"token_type\":\"Bearer\",\"expires_in\":3599}");
        var provider = MTNMoMoProvider(handler);

        using var cts = new CancellationTokenSource();
        var payTask = Task.Run(() => provider.ProcessPaymentAsync(SimplePayment("256771234567", "EUR"), cts.Token));
        Assert.True(handler.AuthCalled.Wait(TimeSpan.FromSeconds(5)), "OAuth did not complete in time");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payTask);
        Assert.Equal(1, handler.AuthCount);
    }

    // -------------------------------------------------------------------
    //  AirtelMoney — OAuth provider
    // -------------------------------------------------------------------
    private static AirtelMoneyPaymentProvider AirtelProvider(HttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new AirtelMoneyOptions
            {
                ClientId = "c", ClientSecret = "s", Country = "KE", Currency = "KES",
                CallbackUrl = "https://example.com/cb", WebhookSecret = "wh", UseSandbox = true
            }),
            NullLogger<AirtelMoneyPaymentProvider>.Instance);

    [Fact]
    public async Task AirtelMoney_ProcessPaymentAsync_PreCancelled_DoesNotCallHandler()
    {
        var handler = new ThrowingHandler();
        var provider = AirtelProvider(handler);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ProcessPaymentAsync(SimplePayment("254712345678", "KES"), cts.Token));
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task AirtelMoney_CancelAfterOAuthFetch_PropagatesOperationCanceled()
    {
        var handler = new OAuthThenBlockHandler(
            u => u.PathAndQuery.Contains("auth/oauth2/token", StringComparison.Ordinal),
            "{\"access_token\":\"airtel-tok\",\"token_type\":\"Bearer\",\"expires_in\":3599}");
        var provider = AirtelProvider(handler);

        using var cts = new CancellationTokenSource();
        var payTask = Task.Run(() => provider.ProcessPaymentAsync(SimplePayment("254712345678", "KES"), cts.Token));
        Assert.True(handler.AuthCalled.Wait(TimeSpan.FromSeconds(5)), "OAuth did not complete in time");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => payTask);
        Assert.Equal(1, handler.AuthCount);
    }
}
