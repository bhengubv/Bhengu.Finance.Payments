// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Text;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Bhengu.Finance.Payments.MercadoPago.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.MercadoPago;

public class MercadoPagoQrCodeProviderTests
{
    // 1x1 transparent PNG, base64-encoded. Sufficient to verify the Convert.FromBase64String round-trip.
    private const string TinyPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=";

    private static MercadoPagoQrCodeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new MercadoPagoOptions { AccessToken = "TEST-token" }),
            NullLogger<MercadoPagoQrCodeProvider>.Instance);

    private static QrCodeRequest SampleRequest(QrFormat format = QrFormat.Payload, decimal? amount = 99.90m) => new()
    {
        Amount = amount,
        Currency = "BRL",
        Description = "Pix charge",
        MerchantReference = "order-001",
        Format = format
    };

    private static string PixResponse(long id, string status, decimal amount = 99.90m, string? expiry = null) =>
        "{\"id\":" + id +
        ",\"status\":\"" + status + "\"" +
        ",\"currency_id\":\"BRL\"" +
        ",\"transaction_amount\":" + amount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
        (expiry is null ? string.Empty : ",\"date_of_expiration\":\"" + expiry + "\"") +
        ",\"point_of_interaction\":{\"transaction_data\":{" +
            "\"qr_code\":\"00020126580014BR.GOV.BCB.PIX...XXX5204000053039865802BR6304ABCD\"" +
            ",\"qr_code_base64\":\"" + TinyPngBase64 + "\"" +
        "}}}";

    [Fact]
    public async Task GenerateQrAsync_ReturnsBRCodePayload_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/v1/payments", req.RequestUri!.PathAndQuery);
            Assert.True(req.Headers.Contains("X-Idempotency-Key"));
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, PixResponse(98765, "pending", expiry: "2026-06-03T11:00:00.000-03:00"));
        });
        var provider = Create(handler);

        var qr = await provider.GenerateQrAsync(SampleRequest());

        Assert.Equal("98765", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.NotNull(qr.Payload);
        Assert.StartsWith("00020126", qr.Payload!, StringComparison.Ordinal);
        Assert.Null(qr.ImageBytes);
        Assert.Equal(99.90m, qr.Amount);
        Assert.Equal("BRL", qr.Currency);
    }

    [Fact]
    public async Task GenerateQrAsync_DecodesPngBytes_WhenFormatIsPng()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.Created, PixResponse(11111, "pending", amount: 50m)));
        var provider = Create(handler);

        var qr = await provider.GenerateQrAsync(SampleRequest(QrFormat.Png, amount: 50m));

        Assert.Equal(QrFormat.Png, qr.Format);
        Assert.Null(qr.Payload);
        Assert.NotNull(qr.ImageBytes);
        var expected = Convert.FromBase64String(TinyPngBase64);
        Assert.Equal(expected, qr.ImageBytes);
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
    public async Task GenerateQrAsync_PassesIdempotencyKey_WhenSupplied()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Idempotency-Key", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.Created, PixResponse(222, "pending"));
        });
        var provider = Create(handler);
        var req = SampleRequest() with { IdempotencyKey = "idem-pix-1" };

        await provider.GenerateQrAsync(req);
        Assert.Equal("idem-pix-1", header);
    }

    [Fact]
    public async Task GenerateQrAsync_PropagatesExpiresAt_FromRequest()
    {
        var expiry = DateTime.UtcNow.AddMinutes(15);
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.Created, PixResponse(333, "pending")));
        var provider = Create(handler);
        var qr = await provider.GenerateQrAsync(SampleRequest() with { ExpiresAt = expiry });
        Assert.Equal(expiry, qr.ExpiresAt);
    }

    [Fact]
    public async Task GenerateQrAsync_Throws_When4xx()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, "invalid request"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.GenerateQrAsync(SampleRequest()));
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsApprovedToCompleted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("/v1/payments/98765", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":98765,"status":"approved"}""");
        });
        var provider = Create(handler);
        Assert.Equal(PaymentStatus.Completed, await provider.GetQrStatusAsync("98765"));
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsPendingToPending()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":1,"status":"pending"}"""));
        var provider = Create(handler);
        Assert.Equal(PaymentStatus.Pending, await provider.GetQrStatusAsync("1"));
    }

    [Fact]
    public async Task GetQrStatusAsync_MapsRejectedToFailed()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":1,"status":"rejected"}"""));
        var provider = Create(handler);
        Assert.Equal(PaymentStatus.Failed, await provider.GetQrStatusAsync("1"));
    }
}
