// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Bhengu.Finance.Payments.PagSeguro.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PagSeguro;

public class PagSeguroQrCodeProviderTests
{
    private static PagSeguroQrCodeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PagSeguroOptions { ApiToken = "pagbank-test-token" }),
            NullLogger<PagSeguroQrCodeProvider>.Instance);

    private static QrCodeRequest SampleRequest(QrFormat format = QrFormat.Payload, decimal? amount = 99.90m) => new()
    {
        Amount = amount,
        Currency = "BRL",
        Description = "Pix charge",
        MerchantReference = "order-001",
        Format = format
    };

    [Fact]
    public async Task GenerateQrAsync_PostsOrderWithPixBlock_AndReturnsPayload()
    {
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/orders", req.RequestUri!.PathAndQuery);
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":"ORDE_pix_1","reference_id":"order-001","qr_codes":[{"id":"QR_1","text":"00020126580014BR.GOV.BCB.PIX...XXX5204000053039865802BR6304ABCD","expiration_date":"2026-06-03T11:00:00-03:00","amount":{"value":9990,"currency":"BRL"},"links":[{"rel":"QRCODE.PNG","href":"https://example.com/qr.png","media":"image/png","type":"image"}]}]}
                """);
        });
        var provider = Create(handler);

        var qr = await provider.GenerateQrAsync(SampleRequest());

        Assert.Equal("ORDE_pix_1", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.NotNull(qr.Payload);
        Assert.StartsWith("00020126", qr.Payload!, StringComparison.Ordinal);
        Assert.Null(qr.ImageBytes);
        Assert.Equal(99.90m, qr.Amount);
        Assert.Equal("BRL", qr.Currency);
        Assert.NotNull(sentBody);
        Assert.Contains("\"value\":9990", sentBody!);
        Assert.Contains("\"currency\":\"BRL\"", sentBody);
    }

    [Fact]
    public async Task GenerateQrAsync_ThrowsForSvg()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, "{}"));
        var provider = Create(handler);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.GenerateQrAsync(SampleRequest(QrFormat.Svg)));
        Assert.Contains("SVG", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateQrAsync_PropagatesExpiresAt_ToRequestBody()
    {
        var expiry = new DateTime(2026, 6, 3, 11, 0, 0, DateTimeKind.Utc);
        string? sentBody = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, """
                {"id":"ORDE_pix_2","qr_codes":[{"id":"QR_2","text":"00020126","links":[]}]}
                """);
        });
        var provider = Create(handler);
        var qr = await provider.GenerateQrAsync(SampleRequest() with { ExpiresAt = expiry });

        Assert.Equal(expiry, qr.ExpiresAt);
        Assert.NotNull(sentBody);
        Assert.Contains("expiration_date", sentBody!);
    }

    [Fact]
    public async Task GenerateQrAsync_Throws_When4xx()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.UnprocessableEntity, "invalid request"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.GenerateQrAsync(SampleRequest()));
    }

    [Fact]
    public async Task GenerateQrAsync_Throws_WhenNoQrInResponse()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.Created, """{"id":"ORDE_x","qr_codes":[]}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GenerateQrAsync(SampleRequest()));
    }

    [Fact]
    public async Task GenerateQrAsync_Throws_OnRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GenerateQrAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsPaidToCompleted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/orders/ORDE_pix_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"ORDE_pix_1","status":"PAID","charges":[{"id":"CHAR_1","status":"PAID"}]}
                """);
        });
        var provider = Create(handler);
        Assert.Equal(PaymentStatus.Completed, await provider.GetQrStatusAsync("ORDE_pix_1"));
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsWaitingToPending()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"ORDE_x","status":"WAITING","charges":[{"id":"C","status":"WAITING"}]}
                """));
        var provider = Create(handler);
        Assert.Equal(PaymentStatus.Pending, await provider.GetQrStatusAsync("ORDE_x"));
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsExpiredToCancelled()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"ORDE_e","status":"EXPIRED","charges":[]}"""));
        var provider = Create(handler);
        Assert.Equal(PaymentStatus.Cancelled, await provider.GetQrStatusAsync("ORDE_e"));
    }
}
