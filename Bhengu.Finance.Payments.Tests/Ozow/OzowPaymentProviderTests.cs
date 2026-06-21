// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Ozow.Configuration;
using Bhengu.Finance.Payments.Ozow.Internals;
using Bhengu.Finance.Payments.Ozow.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Ozow;

public class OzowPaymentProviderTests
{
    private static OzowPaymentProvider Create(StubHttpMessageHandler handler, OzowOptions? opts = null)
    {
        opts ??= new OzowOptions { SiteCode = "TEST", PrivateKey = "priv", ApiKey = "apik", CountryCode = "ZA", UseSandbox = true };
        var http = new HttpClient(handler);
        return new OzowPaymentProvider(http, Options.Create(opts), NullLogger<OzowPaymentProvider>.Instance,
            new OzowIdempotencyCache(new InMemoryBhenguDistributedCache()));
    }

    // The charge is a redirect — it issues no HTTP. This handler fails the test if any HTTP call is made.
    private static StubHttpMessageHandler NoHttp() =>
        new((_, _) => throw new InvalidOperationException("The Ozow charge must not issue an HTTP request — it is a redirect."));

    private static PaymentRequest SamplePayment() => new()
    {
        PaymentMethodToken = "ozow-ref-1",
        Amount = 250m,
        Currency = "ZAR",
        Description = "Ozow test"
    };

    /// <summary>Independently recompute Ozow's HashCheck so the assertion isn't circular against the provider.</summary>
    private static string ExpectedHash(string siteCode, string countryCode, string currencyCode, string amount,
        string transactionReference, string bankReference, string cancelUrl, string errorUrl, string successUrl,
        string notifyUrl, string isTest, string privateKey)
    {
        var concat = string.Concat(siteCode, countryCode, currencyCode, amount, transactionReference,
            bankReference, cancelUrl, errorUrl, successUrl, notifyUrl, isTest, privateKey).ToLowerInvariant();
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(concat));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static NameValueCollection QueryOf(string url) => HttpUtility.ParseQueryString(new Uri(url).Query);

    [Fact]
    public void Constructor_Throws_WhenSiteCodeMissing()
    {
        var http = new HttpClient(NoHttp());
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { PrivateKey = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance,
                new OzowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenPrivateKeyMissing()
    {
        var http = new HttpClient(NoHttp());
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", ApiKey = "y" }), NullLogger<OzowPaymentProvider>.Instance,
                new OzowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Constructor_Throws_WhenApiKeyMissing()
    {
        var http = new HttpClient(NoHttp());
        Assert.Throws<ProviderConfigurationException>(() =>
            new OzowPaymentProvider(http, Options.Create(new OzowOptions { SiteCode = "x", PrivateKey = "y" }), NullLogger<OzowPaymentProvider>.Instance,
                new OzowIdempotencyCache(new InMemoryBhenguDistributedCache())));
    }

    [Fact]
    public void Capabilities_IncludeRedirectFlowAndIdempotency()
    {
        var provider = Create(NoHttp());
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.RedirectFlow));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.TypedWebhooks));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Idempotency));
        Assert.True(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.PartialRefund));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.Payout));
        Assert.False(provider.Capabilities.HasFlag(Bhengu.Finance.Payments.Core.ProviderCapabilities.ThreeDSecure));
    }

    [Fact]
    public async Task ProcessPaymentAsync_ReturnsPendingRedirect_ToPayOzow_NoHttpCall()
    {
        var provider = Create(NoHttp());
        var response = await provider.ProcessPaymentAsync(SamplePayment());

        Assert.Equal(PaymentStatus.Pending, response.Status);
        Assert.NotNull(response.RedirectUrl);
        Assert.StartsWith("https://pay.ozow.com/", response.RedirectUrl);
        // Until the payer lands on Ozow there is no Ozow tx id — the merchant reference is the correlation key.
        Assert.Equal("ozow-ref-1", response.GatewayReference);
        Assert.Equal(250m, response.Amount);
        Assert.Equal("ZAR", response.Currency);
    }

    [Fact]
    public async Task ProcessPaymentAsync_RedirectCarriesAllPostVariables_InTableOrder()
    {
        var provider = Create(NoHttp());
        var response = await provider.ProcessPaymentAsync(SamplePayment() with
        {
            Metadata = new Dictionary<string, string>
            {
                ["transaction_reference"] = "REF-123",
                ["success_url"] = "https://shop.example/ok",
                ["cancel_url"] = "https://shop.example/cancel",
                ["error_url"] = "https://shop.example/err",
                ["notify_url"] = "https://shop.example/notify"
            }
        });

        var q = QueryOf(response.RedirectUrl!);
        Assert.Equal("TEST", q["SiteCode"]);
        Assert.Equal("ZA", q["CountryCode"]);
        Assert.Equal("ZAR", q["CurrencyCode"]);
        Assert.Equal("250.00", q["Amount"]);
        Assert.Equal("REF-123", q["TransactionReference"]);
        Assert.Equal("Ozow test", q["BankReference"]);
        Assert.Equal("https://shop.example/cancel", q["CancelUrl"]);
        Assert.Equal("https://shop.example/err", q["ErrorUrl"]);
        Assert.Equal("https://shop.example/ok", q["SuccessUrl"]);
        Assert.Equal("https://shop.example/notify", q["NotifyUrl"]);
        Assert.Equal("true", q["IsTest"]); // UseSandbox=true => lowercase "true"
        Assert.False(string.IsNullOrEmpty(q["HashCheck"]));
    }

    [Fact]
    public async Task ProcessPaymentAsync_HashCheck_MatchesSha512OfLowercasedConcatPlusPrivateKey()
    {
        var provider = Create(NoHttp());
        var response = await provider.ProcessPaymentAsync(SamplePayment() with
        {
            Metadata = new Dictionary<string, string>
            {
                ["transaction_reference"] = "REF-123",
                ["success_url"] = "https://shop.example/ok"
            }
        });

        var q = QueryOf(response.RedirectUrl!);
        var expected = ExpectedHash(
            siteCode: "TEST",
            countryCode: "ZA",
            currencyCode: "ZAR",
            amount: "250.00",
            transactionReference: "REF-123",
            bankReference: "Ozow test",
            cancelUrl: "",
            errorUrl: "",
            successUrl: "https://shop.example/ok",
            notifyUrl: "",
            isTest: "true",
            privateKey: "priv");

        Assert.Equal(expected, q["HashCheck"]);
    }

    [Fact]
    public async Task ProcessPaymentAsync_IsTestFalse_WhenNotSandbox()
    {
        var provider = Create(NoHttp(), new OzowOptions
        {
            SiteCode = "TEST", PrivateKey = "priv", ApiKey = "apik", CountryCode = "ZA", UseSandbox = false
        });
        var response = await provider.ProcessPaymentAsync(SamplePayment());
        var q = QueryOf(response.RedirectUrl!);
        Assert.Equal("false", q["IsTest"]);
    }

    [Fact]
    public async Task ProcessPaymentAsync_TruncatesBankReference_To20Chars()
    {
        var provider = Create(NoHttp());
        var response = await provider.ProcessPaymentAsync(SamplePayment() with
        {
            Description = "This description is way longer than twenty characters"
        });
        var q = QueryOf(response.RedirectUrl!);
        Assert.Equal(20, q["BankReference"]!.Length);
        Assert.Equal("This description is ", q["BankReference"]);
    }

    [Fact]
    public async Task ProcessPaymentAsync_Dedupes_OnSameIdempotencyKey()
    {
        var provider = Create(NoHttp());
        var r1 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "key-1" });
        var r2 = await provider.ProcessPaymentAsync(SamplePayment() with { IdempotencyKey = "key-1" });
        Assert.Equal(r1.RedirectUrl, r2.RedirectUrl);
        Assert.Equal(r1.GatewayReference, r2.GatewayReference);
    }

    [Fact]
    public async Task GetTransactionByReferenceAsync_CallsApiOzow_WithApiKeyHeader()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            captured = req;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """[{"transactionId":"ozow_tx_1","status":"Complete"}]""");
        });
        var provider = Create(handler);

        var body = await provider.GetTransactionByReferenceAsync("REF-123");

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured!.Method);
        Assert.Equal("https://api.ozow.com/GetTransactionByReference?siteCode=TEST&transactionReference=REF-123",
            captured.RequestUri!.ToString());
        Assert.True(captured.Headers.Contains("ApiKey"));
        Assert.Contains("ozow_tx_1", body);
    }

    [Fact]
    public async Task GetTransactionAsync_CallsApiOzow_GetTransaction()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            captured = req;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"transactionId":"ozow_tx_9","status":"Complete"}""");
        });
        var provider = Create(handler);

        await provider.GetTransactionAsync("ozow_tx_9");

        Assert.Equal("https://api.ozow.com/GetTransaction?siteCode=TEST&transactionId=ozow_tx_9",
            captured!.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetTransactionByReferenceAsync_Throws429AsProviderRateLimitException()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate limited"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GetTransactionByReferenceAsync("REF-1"));
    }

    [Fact]
    public async Task GetTransactionByReferenceAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("network down"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GetTransactionByReferenceAsync("REF-1"));
    }

    [Fact]
    public async Task ProcessRefundAsync_ReturnsResponse_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("refund", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"refundId":"ozow_rf_1","status":"completed"}""");
        });
        var provider = Create(handler);
        var refund = await provider.ProcessRefundAsync(new RefundRequest
        {
            GatewayReference = "ozow_tx_1",
            Amount = 100m,
            Reason = "Customer requested"
        });
        Assert.Equal("ozow_rf_1", refund.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, refund.Status);
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = Create(NoHttp());
        Assert.False(provider.VerifyWebhookSignature("anything", "tampered"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string priv = "priv";
        const string payload = """{"transactionId":"ozow_99","status":"complete"}""";
        var hash = SHA512.HashData(Encoding.UTF8.GetBytes(payload + priv));
        var validSig = Convert.ToHexString(hash).ToLowerInvariant();

        var provider = Create(NoHttp());
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeSucceeded_WhenComplete()
    {
        var provider = Create(NoHttp());
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","transactionReference":"ref_99","status":"complete","amount":250.00}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal("ref_99", typed.GatewayReference);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(250m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhookAsync_FallsBackToTransactionId_WhenNoReference()
    {
        var provider = Create(NoHttp());
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","status":"complete","amount":250.00}
            """);
        Assert.NotNull(evt);
        Assert.Equal("ozow_99", evt!.GatewayReference);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsTypedChargeFailed_WhenError()
    {
        var provider = Create(NoHttp());
        var evt = await provider.ParseWebhookAsync("""
            {"transactionId":"ozow_99","status":"error","amount":250.00,"statusMessage":"declined"}
            """);
        Assert.NotNull(evt);
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal("declined", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = Create(NoHttp());
        Assert.Null(await provider.ParseWebhookAsync("not json"));
    }
}
