// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.BricsPay;

/// <summary>
/// Tests the rebuilt BRICS Pay QR (Internet Acquiring) provider against its published protocol:
/// create transaction (POST /ia/api), status (GET /ia/get), refund (POST /ia/refund), callback parsing,
/// and asymmetric signing. See BRICS_PAY_API_REFERENCE.md.
/// </summary>
public class BricsPayPaymentProviderTests
{
    private static string TestKeyPem()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return ec.ExportPkcs8PrivateKeyPem();
    }

    private static (BricsPayPaymentProvider Provider, List<(HttpMethod Method, string Uri, string Body)> Requests)
        MakeProvider(Func<HttpRequestMessage, HttpResponseMessage> respond, BricsPayOptions? opts = null)
    {
        var requests = new List<(HttpMethod, string, string)>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            var body = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
            requests.Add((req.Method, req.RequestUri!.ToString(), body));
            return respond(req);
        });

        opts ??= new BricsPayOptions
        {
            TerminalId = "POS-1",
            BaseUrl = "https://terminal.brics.example",
            PrivateKeyPem = TestKeyPem()
        };

        var provider = new BricsPayPaymentProvider(
            new HttpClient(handler), Options.Create(opts), NullLogger<BricsPayPaymentProvider>.Instance);
        return (provider, requests);
    }

    private static QrCodeRequest SampleRequest() => new()
    {
        Amount = 100m,
        Currency = "ZAR",
        Description = "Test order",
        MerchantReference = "ORDER-1",
        PayerIdentifier = "deadbeef"   // stands in for SHA256(IP + User-Agent)
    };

    [Fact]
    public void Constructor_Throws_WhenTerminalIdMissing()
    {
        var ex = Assert.Throws<ProviderConfigurationException>(() => new BricsPayPaymentProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new BricsPayOptions { BaseUrl = "https://x", PrivateKeyPem = TestKeyPem() }),
            NullLogger<BricsPayPaymentProvider>.Instance));
        Assert.Contains("TerminalId", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenBaseUrlMissing()
    {
        var ex = Assert.Throws<ProviderConfigurationException>(() => new BricsPayPaymentProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new BricsPayOptions { TerminalId = "POS-1", PrivateKeyPem = TestKeyPem() }),
            NullLogger<BricsPayPaymentProvider>.Instance));
        Assert.Contains("BaseUrl", ex.Message);
    }

    [Fact]
    public void Constructor_Throws_WhenPrivateKeyMissing()
    {
        var ex = Assert.Throws<ProviderConfigurationException>(() => new BricsPayPaymentProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new BricsPayOptions { TerminalId = "POS-1", BaseUrl = "https://x" }),
            NullLogger<BricsPayPaymentProvider>.Instance));
        Assert.Contains("PrivateKeyPem", ex.Message);
    }

    [Fact]
    public async Task GenerateQrAsync_CreatesTransaction_ReturnsPaymentUrl_AndSigns()
    {
        var (provider, requests) = MakeProvider(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"URL":"https://pay.brics.example/qr/abc"}"""));

        var qr = await provider.GenerateQrAsync(SampleRequest());

        Assert.Equal("ORDER-1", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.Equal("https://pay.brics.example/qr/abc", qr.Payload);
        Assert.Equal(100m, qr.Amount);
        Assert.Equal("ZAR", qr.Currency);

        var req = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("/ia/api/", req.Uri);
        Assert.Contains("signature=", req.Uri);
        Assert.Contains("\"Pos\":\"POS-1\"", req.Body);
        Assert.Contains("\"Sequence\":\"ORDER-1\"", req.Body);
        Assert.Contains("\"User\":\"deadbeef\"", req.Body);
    }

    [Fact]
    public async Task GenerateQrAsync_Throws_WhenAmountMissing_AndMakesNoCall()
    {
        var (provider, requests) = MakeProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.GenerateQrAsync(SampleRequest() with { Amount = null }));
        Assert.Contains("amount", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task GenerateQrAsync_Throws_WhenPayerIdentifierMissing_AndMakesNoCall()
    {
        var (provider, requests) = MakeProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.GenerateQrAsync(SampleRequest() with { PayerIdentifier = null }));
        Assert.Contains("PayerIdentifier", ex.Message);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task GetQrStatusAsync_PaidAndProcessed_ReturnsCompleted_AndSignsGet()
    {
        var (provider, requests) = MakeProvider(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"Transaction":"T-1","Paid":true,"Processed":true,"Amount":"100.00","Currency":{"Code":710,"Precision":2,"Name":"Rand","Symbol":"R"}}"""));

        var status = await provider.GetQrStatusAsync("ORDER-1");

        Assert.Equal(PaymentStatus.Completed, status);
        var req = Assert.Single(requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Contains("/ia/get/", req.Uri);
        Assert.Contains("pos=POS-1", req.Uri);
        Assert.Contains("sequence=ORDER-1", req.Uri);
        Assert.Contains("signature=", req.Uri);
    }

    [Fact]
    public async Task GetQrStatusAsync_NotProcessed_ReturnsPending()
    {
        var (provider, _) = MakeProvider(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"Paid":false,"Processed":false,"Amount":"100.00"}"""));
        Assert.Equal(PaymentStatus.Pending, await provider.GetQrStatusAsync("ORDER-1"));
    }

    [Fact]
    public async Task GetTransactionAsync_MapsRichStatus()
    {
        var (provider, _) = MakeProvider(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"Transaction":"T-9","Paid":true,"Processed":true,"Amount":"250.50","Currency":{"Code":710,"Precision":2,"Name":"South African Rand","Symbol":"R"},"Time":{"Created":"2026-06-21T08:00:00Z","Processed":"2026-06-21T08:01:00Z","Timeout":"2026-06-21T08:05:00Z"}}"""));

        var s = await provider.GetTransactionAsync("ORDER-9");

        Assert.Equal("T-9", s.Transaction);
        Assert.True(s.Paid);
        Assert.True(s.Processed);
        Assert.Equal(PaymentStatus.Completed, s.Status);
        Assert.Equal(250.50m, s.Amount);
        Assert.Equal(710, s.CurrencyCode);
        Assert.Equal("South African Rand", s.CurrencyName);
        Assert.NotNull(s.ProcessedUtc);
    }

    [Fact]
    public async Task GetTransactionAsync_FailedAuth_ReturnsFailedWithError()
    {
        var (provider, _) = MakeProvider(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"Paid":false,"Processed":true,"Amount":"100.00","Error":{"Code":"51","Message":"Insufficient funds"}}"""));

        var s = await provider.GetTransactionAsync("ORDER-2");
        Assert.Equal(PaymentStatus.Failed, s.Status);
        Assert.Equal("51", s.ErrorCode);
        Assert.Equal("Insufficient funds", s.ErrorMessage);
    }

    [Fact]
    public async Task RefundAsync_PostsRefund_WithReferenceAndNewSequence()
    {
        var (provider, requests) = MakeProvider(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"Transaction":"RF-1","Paid":false,"Processed":true,"Amount":"50.00","Reference":"T-1"}"""));

        var s = await provider.RefundAsync(originalTransaction: "T-1", refundSequence: "REFUND-1", amount: 50m);

        Assert.Equal("RF-1", s.Transaction);
        var req = Assert.Single(requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Contains("/ia/refund", req.Uri);
        Assert.Contains("signature=", req.Uri);
        Assert.Contains("\"Reference\":\"T-1\"", req.Body);
        Assert.Contains("\"Sequence\":\"REFUND-1\"", req.Body);
        Assert.Contains("\"Amount\":\"50\"", req.Body);
    }

    [Fact]
    public void ParseCallback_ParsesBody_AndMapsStatus()
    {
        var (provider, _) = MakeProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var cb = provider.ParseCallback(
            """{"POS":"POS-1","Sequence":"ORDER-1","Transaction":"T-1","Paid":true,"Processed":true,"Amount":"100.00","Currency":{"Code":710}}""");

        Assert.NotNull(cb);
        Assert.Equal("POS-1", cb!.Pos);
        Assert.Equal("ORDER-1", cb.Sequence);
        Assert.Equal("T-1", cb.Transaction);
        Assert.Equal(PaymentStatus.Completed, cb.Status);
        Assert.Equal(100m, cb.Amount);
        Assert.Equal(710, cb.CurrencyCode);
    }

    [Fact]
    public void ParseCallback_ReturnsNull_ForInvalidJson()
    {
        var (provider, _) = MakeProvider(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.Null(provider.ParseCallback("not json"));
    }

    [Fact]
    public async Task GetQrStatusAsync_429_ThrowsProviderRateLimit()
    {
        var (provider, _) = MakeProvider(_ => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "rate"));
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GetQrStatusAsync("ORDER-1"));
    }

    [Fact]
    public async Task GetTransactionAsync_4xx_ThrowsPaymentDeclined()
    {
        var (provider, _) = MakeProvider(_ => StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "bad"));
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.GetTransactionAsync("ORDER-1"));
    }

    [Fact]
    public async Task GenerateQrAsync_NetworkError_ThrowsProviderUnavailable()
    {
        var (provider, _) = MakeProvider(_ => throw new HttpRequestException("down"));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GenerateQrAsync(SampleRequest()));
    }
}
