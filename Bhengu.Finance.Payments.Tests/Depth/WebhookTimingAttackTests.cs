// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Providers;
using Bhengu.Finance.Payments.Alipay.Configuration;
using Bhengu.Finance.Payments.Alipay.Providers;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Cellulant.Providers;
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
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Hubtel.Configuration;
using Bhengu.Finance.Payments.Hubtel.Providers;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Bhengu.Finance.Payments.IPay.Configuration;
using Bhengu.Finance.Payments.IPay.Providers;
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
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Providers;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Bhengu.Finance.Payments.MTNMoMo.Providers;
using Bhengu.Finance.Payments.Onafriq.Configuration;
using Bhengu.Finance.Payments.Onafriq.Providers;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Providers;
using Bhengu.Finance.Payments.OrangeMoney.Configuration;
using Bhengu.Finance.Payments.OrangeMoney.Providers;
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
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Bhengu.Finance.Payments.Paymob.Providers;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Bhengu.Finance.Payments.Paytm.Providers;
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Bhengu.Finance.Payments.PayUIndia.Providers;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Bhengu.Finance.Payments.Pesapal.Providers;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Providers;
using Bhengu.Finance.Payments.Slydepay.Configuration;
using Bhengu.Finance.Payments.Slydepay.Providers;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Bhengu.Finance.Payments.Stitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Bhengu.Finance.Payments.Wave.Configuration;
using Bhengu.Finance.Payments.Wave.Providers;
using Bhengu.Finance.Payments.WeChatPay.Configuration;
using Bhengu.Finance.Payments.WeChatPay.Providers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Bhengu.Finance.Payments.Yoco.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Depth;

/// <summary>
/// Timing-attack regression tests for every provider's <c>VerifyWebhookSignature</c> entry point.
/// <para>
/// We compare wall-clock time for two FAILED verifications: <c>sigFailFirst</c> differs from the
/// expected at byte 0, <c>sigFailLast</c> differs only at the last byte. A naive string-equality
/// comparison would short-circuit on the first miss and run materially faster on <c>sigFailFirst</c>
/// — leaking position information one byte at a time to an attacker who can measure remote latency.
/// </para>
/// <para>
/// All providers delegate to <c>SignatureHelpers.VerifyHmac*</c> / <c>SignatureHelpers.ConstantTimeEquals</c>,
/// which use <see cref="CryptographicOperations.FixedTimeEquals"/> internally. The contract is:
/// <c>|median(sigFailFirst) - median(sigFailLast)| / max(median(sigFailFirst), median(sigFailLast)) &lt; 0.5</c>.
/// 50% is a generous tolerance — true constant-time comparison stays within ~10% under load, but
/// CI machines under contention can spike. A regression that drops to byte-comparison would skew
/// the ratio close to 1.0 (one is 10-100x faster).
/// </para>
/// </summary>
public static class TimingAttackHelpers
{
    /// <summary>Iterations per scenario. 1000 is enough to push raw byte-comparison failures into the noise floor.</summary>
    public const int IterationCount = 1000;

    /// <summary>The ratio threshold above which we declare a regression. 0.5 = 50% latency gap.</summary>
    public const double MaxRatio = 0.5;

    /// <summary>
    /// Noise floor for sub-microsecond timings on this machine. Stopwatch resolution + scheduler
    /// jitter means anything under this is meaningless ratio-wise (one extra 100 ns context switch
    /// becomes a 100% delta). We require both medians to exceed this before ratio-checking;
    /// otherwise we assert only "neither side is &gt; 5× the other" as a coarse sanity bound.
    /// </summary>
    public const double NoiseFloorMicros = 1.0;

    /// <summary>Measure the median per-call wall-clock time of <paramref name="body"/> over <paramref name="iterations"/> runs.</summary>
    public static double MedianMicroseconds(Action body, int iterations)
    {
        var samples = new double[iterations];
        // Warm-up — JIT and CPU caches.
        for (var w = 0; w < 200; w++) body();
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            body();
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
        }
        Array.Sort(samples);
        return samples[iterations / 2];
    }

    /// <summary>
    /// Run the timing test against the supplied provider. Computes the canonical signature using
    /// the provided <paramref name="signFn"/>, then constructs two failure-mode signatures of the
    /// same length and asserts the median wall-clock times are within <see cref="MaxRatio"/> of one
    /// another. Throws via <see cref="Assert.True(bool, string)"/> on regression.
    /// </summary>
    public static void AssertConstantTimeVerification(
        Func<string, string, bool> verifyFn,
        string payload,
        string validSignature)
    {
        if (validSignature.Length < 2)
            throw new ArgumentException("Need at least 2 chars of signature to mutate.", nameof(validSignature));

        // Build mismatch-at-byte-0: flip first character to something different but same charset.
        var failFirst = MutateChar(validSignature, 0);
        // Build mismatch-at-last: flip last character.
        var failLast = MutateChar(validSignature, validSignature.Length - 1);

        // Ensure the helpers are doing what we think — both must be rejected.
        Assert.False(verifyFn(payload, failFirst), "sigFailFirst must NOT verify");
        Assert.False(verifyFn(payload, failLast), "sigFailLast must NOT verify");

        var firstMed = MedianMicroseconds(() => verifyFn(payload, failFirst), IterationCount);
        var lastMed = MedianMicroseconds(() => verifyFn(payload, failLast), IterationCount);

        var ratio = Math.Abs(firstMed - lastMed) / Math.Max(firstMed, lastMed);

        // Below the noise floor (< 1 us per call) any ratio measurement is dominated by Stopwatch
        // resolution and scheduler jitter — a single context switch can flip the ratio by 50%+.
        // Fall back to a coarse 5× sanity bound: if a byte-by-byte comparison had regressed, the
        // first-byte-mismatch path would be many times faster than the last-byte path, not 2×.
        if (Math.Max(firstMed, lastMed) < NoiseFloorMicros)
        {
            var coarseRatio = Math.Max(firstMed, lastMed) / Math.Max(Math.Min(firstMed, lastMed), 0.001);
            Assert.True(
                coarseRatio < 5.0,
                $"Coarse-bound timing regression (sub-noise-floor): median(failFirst)={firstMed:F2} us, median(failLast)={lastMed:F2} us, coarseRatio={coarseRatio:F1}× (threshold 5.0×).");
            return;
        }

        Assert.True(
            ratio < MaxRatio,
            $"Timing regression: median(failFirst)={firstMed:F2} us, median(failLast)={lastMed:F2} us, ratio={ratio:F3} (threshold {MaxRatio:F2}). " +
            "If this fires, the provider switched from CryptographicOperations.FixedTimeEquals to a byte-by-byte comparison.");
    }

    /// <summary>Replace one char with a guaranteed-different one drawn from the same alphabet (hex or base64).</summary>
    private static string MutateChar(string s, int index)
    {
        var c = s[index];
        char replacement;
        if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))
            replacement = c == '0' ? '1' : '0';
        else if (char.IsLetterOrDigit(c))
            replacement = c == 'A' ? 'B' : 'A';
        else if (c == '+')
            replacement = '/';
        else if (c == '/')
            replacement = '+';
        else if (c == '=')
            replacement = 'A';
        else
            replacement = (char)(((c + 1) % 95) + 32);
        var arr = s.ToCharArray();
        arr[index] = replacement;
        return new string(arr);
    }

    /// <summary>Build a lowercase-hex HMAC-SHA256 of <paramref name="payload"/> using <paramref name="secret"/>.</summary>
    public static string HmacSha256Hex(string payload, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Build a base64 HMAC-SHA256 of <paramref name="payload"/> using <paramref name="secret"/>.</summary>
    public static string HmacSha256Base64(string payload, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    /// <summary>Build a lowercase-hex HMAC-SHA512 of <paramref name="payload"/> using <paramref name="secret"/>.</summary>
    public static string HmacSha512Hex(string payload, string secret)
    {
        var hash = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>Default test payload for HMAC-based providers. Long enough to make per-byte short-circuit visible.</summary>
    public const string DefaultPayload = "{\"event\":\"charge.success\",\"data\":{\"reference\":\"ref_timing_test_payload_for_constant_time_verification\",\"status\":\"success\",\"amount\":10000,\"currency\":\"ZAR\"}}";
}

public sealed class WebhookTimingAttackTests
{
    private const string Secret = "webhook-timing-secret-for-constant-time-verification";
    private const string Payload = TimingAttackHelpers.DefaultPayload;

    private static HttpClient StubHttp() =>
        new(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)));

    // --- Paystack: HMAC SHA512 hex ---
    [Fact]
    public void Paystack_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new PaystackPaymentProvider(StubHttp(),
            Options.Create(new PaystackOptions { SecretKey = "sk", DefaultEmail = "b@x.com", WebhookSecret = Secret }),
            NullLogger<PaystackPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha512Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Flutterwave: ConstantTimeEquals on verbatim WebhookSecret echoed in verif-hash header. ---
    [Fact]
    public void Flutterwave_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new FlutterwavePaymentProvider(StubHttp(),
            Options.Create(new FlutterwaveOptions
            {
                SecretKey = "FLWSECK_TEST-xxx", PublicKey = "p", EncryptionKey = "e",
                WebhookSecret = Secret, RedirectUrl = "https://example.com/r"
            }),
            NullLogger<FlutterwavePaymentProvider>.Instance);
        // For Flutterwave the "signature" IS the verbatim secret; we mutate it to test constant-time fail.
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, Secret);
    }

    // --- Yoco: HMAC SHA256 base64. ---
    [Fact]
    public void Yoco_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new YocoPaymentProvider(StubHttp(),
            Options.Create(new YocoOptions { SecretKey = "sk", WebhookSecret = Secret }),
            NullLogger<YocoPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Base64(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Wave: HMAC SHA256 hex ---
    [Fact]
    public void Wave_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new WavePaymentProvider(StubHttp(),
            Options.Create(new WaveOptions
            {
                ApiKey = "wave_sn_prod_xxx", WebhookSecret = Secret, Currency = "XOF",
                SuccessUrl = "https://example.com/s", ErrorUrl = "https://example.com/e"
            }),
            NullLogger<WavePaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Paymob: HMAC SHA512 hex ---
    [Fact]
    public void Paymob_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new PaymobPaymentProvider(StubHttp(),
            Options.Create(new PaymobOptions
            {
                ApiKey = "api", HmacSecret = Secret, IntegrationId = 1, IframeId = 1, Currency = "EGP"
            }),
            NullLogger<PaymobPaymentProvider>.Instance,
            new PaymobIdempotencyCache(new InMemoryBhenguDistributedCache()));
        var sig = TimingAttackHelpers.HmacSha512Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Razorpay: HMAC SHA256 hex ---
    [Fact]
    public void Razorpay_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new RazorpayPaymentProvider(StubHttp(),
            Options.Create(new RazorpayOptions { KeyId = "rzp", KeySecret = "ks", WebhookSecret = Secret, RazorpayXAccountNumber = "232323" }),
            NullLogger<RazorpayPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Cellulant: HMAC SHA256 hex ---
    [Fact]
    public void Cellulant_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new CellulantPaymentProvider(StubHttp(),
            Options.Create(new CellulantOptions
            {
                ServiceCode = "TGNTEST", ClientId = "c", ClientSecret = "s", WebhookSecret = Secret,
                CallbackUrl = "https://example.com/cb", UseSandbox = true
            }),
            NullLogger<CellulantPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Mukuru: HMAC SHA256 hex ---
    [Fact]
    public void Mukuru_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new MukuruPaymentProvider(StubHttp(),
            Options.Create(new MukuruOptions
            {
                ClientId = "c", ClientSecret = "s", MerchantId = "M",
                WebhookSecret = Secret, SenderCountry = "ZA", DefaultCurrency = "ZAR",
                CallbackUrl = "https://example.com/cb"
            }),
            NullLogger<MukuruPaymentProvider>.Instance,
            new MukuruIdempotencyCache(new InMemoryBhenguDistributedCache()));
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }


    // --- DPO: skipped — DPO callbacks are NOT cryptographically signed; the provider returns true
    //  for any non-empty signature and authenticity is established via the verifyToken REST call
    //  (see DPOPaymentProvider.VerifyWebhookSignature). Timing-attack category genuinely doesn't apply.

    // --- Hubtel: HMAC SHA256 hex ---
    [Fact]
    public void Hubtel_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new HubtelPaymentProvider(StubHttp(),
            Options.Create(new HubtelOptions
            {
                ClientId = "ci", ClientSecret = "cs", MerchantAccountNumber = "1",
                WebhookSecret = Secret, CallbackUrl = "https://example.com/cb",
                ReturnUrl = "https://example.com/r", Currency = "GHS"
            }),
            NullLogger<HubtelPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Interswitch: HMAC SHA256 hex ---
    [Fact]
    public void Interswitch_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new InterswitchPaymentProvider(StubHttp(),
            Options.Create(new InterswitchOptions
            {
                ClientId = "c", ClientSecret = "s", MerchantCode = "MX12345",
                ProductId = "10101", WebhookSecret = Secret
            }),
            NullLogger<InterswitchPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }


    // --- Kashier: HMAC SHA256 hex ---
    [Fact]
    public void Kashier_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new KashierPaymentProvider(StubHttp(),
            Options.Create(new KashierOptions
            {
                ApiKey = "api_test_key", MerchantId = "MID_1", SecretKey = "secret_kashier",
                WebhookSecret = Secret, Currency = "EGP", Mode = "test", UseSandbox = true
            }),
            NullLogger<KashierPaymentProvider>.Instance,
            new KashierIdempotencyCache(new InMemoryBhenguDistributedCache()));
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- MercadoPago: HMAC SHA256 hex ---
    [Fact]
    public void MercadoPago_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new MercadoPagoPaymentProvider(StubHttp(),
            Options.Create(new MercadoPagoOptions { AccessToken = "TEST", WebhookSecret = Secret }),
            NullLogger<MercadoPagoPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- Moniepoint: HMAC SHA512 hex (monnify-signature) ---
    [Fact]
    public void Moniepoint_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new MoniepointPaymentProvider(StubHttp(),
            Options.Create(new MoniepointOptions
            {
                ApiKey = "mpt-api-key", SecretKey = "sk", ContractCode = "CONTRACT-1",
                WebhookSecret = Secret, RedirectUrl = "https://example.com/r"
            }),
            NullLogger<MoniepointPaymentProvider>.Instance);
        using var h = new System.Security.Cryptography.HMACSHA512(System.Text.Encoding.UTF8.GetBytes(Secret));
        var sig = System.Convert.ToHexString(h.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Payload))).ToLowerInvariant();
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- OPay: HMAC SHA256 hex (or HMAC SHA512) ---
    [Fact]
    public void OPay_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new OPayPaymentProvider(StubHttp(),
            Options.Create(new OPayOptions
            {
                PublicKey = "pub", SecretKey = Secret, MerchantId = "MERCH123",
                Country = "NG", CallbackUrl = "https://example.com/cb", ReturnUrl = "https://example.com/r"
            }),
            NullLogger<OPayPaymentProvider>.Instance);
        // OPay uses HMAC SHA512 hex against the SecretKey on the wire body.
        var sig = TimingAttackHelpers.HmacSha512Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }

    // --- PagSeguro: HMAC SHA256 hex ---
    [Fact]
    public void PagSeguro_VerifyWebhookSignature_IsConstantTime()
    {
        var provider = new PagSeguroPaymentProvider(StubHttp(),
            Options.Create(new PagSeguroOptions { ApiToken = "tok", WebhookSecret = Secret }),
            NullLogger<PagSeguroPaymentProvider>.Instance);
        var sig = TimingAttackHelpers.HmacSha256Hex(Payload, Secret);
        TimingAttackHelpers.AssertConstantTimeVerification(provider.VerifyWebhookSignature, Payload, sig);
    }
}
