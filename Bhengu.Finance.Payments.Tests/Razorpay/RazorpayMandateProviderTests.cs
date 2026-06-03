// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpayMandateProviderTests
{
    private static RazorpayMandateProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x" }),
            NullLogger<RazorpayMandateProvider>.Instance);

    [Fact]
    public async Task CreateMandateAsync_PostsOrder_AndReturnsPendingMandate()
    {
        string? body = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/orders", req.RequestUri!.PathAndQuery);
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"order_mand_1","entity":"order","status":"created","short_url":"https://rzp.io/i/abc"}""");
        });
        var provider = Create(handler);
        var mandate = await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "cust_1",
            BankAccountToken = "acc_1",
            AmountLimit = 5000m,
            Currency = "INR",
            Description = "Monthly bill"
        });

        Assert.Equal("order_mand_1", mandate.Reference);
        Assert.Equal(MandateStatus.Pending, mandate.Status);
        Assert.Equal("https://rzp.io/i/abc", mandate.AuthorisationUrl);
        Assert.NotNull(body);
        Assert.Contains("\"method\":\"emandate\"", body!);
        Assert.Contains("\"max_amount\":500000", body);
    }

    [Fact]
    public async Task CreateMandateAsync_PassesIdempotencyKey()
    {
        string? header = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            header = req.Headers.TryGetValues("X-Razorpay-IdempotencyKey", out var v) ? string.Join(",", v) : null;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"id":"order_1","entity":"order","status":"created"}""");
        });
        var provider = Create(handler);
        await provider.CreateMandateAsync(new MandateRequest
        {
            CustomerId = "cust_1",
            BankAccountToken = "acc_1",
            AmountLimit = 100m,
            Currency = "INR",
            Description = "x",
            IdempotencyKey = "idem-mandate"
        });
        Assert.Equal("idem-mandate", header);
    }

    [Fact]
    public async Task GetMandateAsync_DeserialisesToken()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/tokens/token_m", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"token_m","entity":"token","customer_id":"cust_1","method":"emandate","recurring_status":"confirmed","max_amount":500000,"confirmed_at":1700000000}""");
        });
        var provider = Create(handler);
        var mandate = await provider.GetMandateAsync("token_m");

        Assert.NotNull(mandate);
        Assert.Equal(MandateStatus.Active, mandate!.Status);
        Assert.Equal(5000m, mandate.AmountLimit);
        Assert.NotNull(mandate.AuthorisedAt);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetMandateAsync("nope"));
    }

    [Fact]
    public async Task CancelMandateAsync_FetchesThenDeletes()
    {
        var calls = new List<string>();
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            calls.Add($"{req.Method} {req.RequestUri!.PathAndQuery}");
            if (req.Method == HttpMethod.Get)
                return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                    """{"id":"token_m","entity":"token","customer_id":"cust_1","recurring_status":"confirmed","max_amount":100000}""");
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"deleted":true}""");
        });
        var provider = Create(handler);
        var result = await provider.CancelMandateAsync("token_m");
        Assert.Equal(MandateStatus.Cancelled, result.Status);
        Assert.Equal(2, calls.Count);
        Assert.StartsWith("GET", calls[0]);
        Assert.StartsWith("DELETE", calls[1]);
    }

    [Fact]
    public async Task ChargeMandateAsync_PostsRecurring_AndMapsCaptured()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/payments/create/recurring", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"pay_rec_1","entity":"payment","status":"captured"}""");
        });
        var provider = Create(handler);
        var payment = await provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "token_m",
            Amount = 100m,
            Currency = "INR",
            Description = "Bill",
            IdempotencyKey = "idem-1"
        });

        Assert.Equal("pay_rec_1", payment.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, payment.Status);
        Assert.Equal(100m, payment.Amount);
    }

    [Fact]
    public async Task ChargeMandateAsync_OnDecline_ThrowsPaymentDeclinedException()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.BadRequest, """{"error":{"code":"BAD_REQUEST_ERROR","description":"Insufficient funds"}}"""));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "token_m",
            Amount = 100m,
            Currency = "INR",
            Description = "Bill"
        }));
    }

    [Fact]
    public async Task ChargeMandateAsync_OnNetworkFailure_ThrowsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ChargeMandateAsync(new MandateChargeRequest
        {
            MandateReference = "token_m",
            Amount = 100m,
            Currency = "INR",
            Description = "Bill"
        }));
    }
}
