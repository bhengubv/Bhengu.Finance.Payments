// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Paystack;

public class PaystackDisputeProviderTests
{
    private static PaystackDisputeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new PaystackOptions { SecretKey = "sk_test_xx" }),
            NullLogger<PaystackDisputeProvider>.Instance);

    [Fact]
    public void Constructor_Throws_WhenSecretKeyMissing() =>
        Assert.Throws<ProviderConfigurationException>(() => new PaystackDisputeProvider(
            new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new PaystackOptions()),
            NullLogger<PaystackDisputeProvider>.Instance));

    [Fact]
    public async Task GetDisputeAsync_ReturnsDispute_OnSuccess()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("dispute/DSP_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"id":1,"status":"awaiting-merchant-feedback","category":"fraud","currency":"NGN","createdAt":"2026-06-01T10:00:00Z","due_at":"2026-06-15T10:00:00Z","transaction":{"reference":"ref_1","amount":10000,"currency":"NGN"}}}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.GetDisputeAsync("DSP_1");

        Assert.NotNull(dispute);
        Assert.Equal("1", dispute!.Reference);
        Assert.Equal("ref_1", dispute.ChargeReference);
        Assert.Equal(DisputeStatus.NeedsResponse, dispute.Status);
        Assert.Equal(100m, dispute.Amount);
        Assert.NotNull(dispute.EvidenceDueBy);
    }

    [Fact]
    public async Task GetDisputeAsync_ReturnsNull_On404()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetDisputeAsync("DSP_missing"));
    }

    [Fact]
    public async Task ListDisputesAsync_ReturnsMappedDisputes()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("dispute?perPage=100", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":[
                    {"id":1,"status":"resolved","resolution":"merchant-won","currency":"NGN","createdAt":"2026-05-01T00:00:00Z","transaction":{"reference":"ref_a","amount":5000,"currency":"NGN"}},
                    {"id":2,"status":"expired","currency":"NGN","createdAt":"2026-05-02T00:00:00Z","transaction":{"reference":"ref_b","amount":7500,"currency":"NGN"}}
                ]}
                """);
        });
        var provider = Create(handler);
        var disputes = await provider.ListDisputesAsync(DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);
        Assert.Equal(2, disputes.Count);
        Assert.Equal(DisputeStatus.Won, disputes[0].Status);
        Assert.Equal(DisputeStatus.Expired, disputes[1].Status);
    }

    [Fact]
    public async Task ListDisputesAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "slow"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.ListDisputesAsync(null, null));
    }

    [Fact]
    public async Task SubmitEvidenceAsync_MovesDisputeToUnderReview()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/evidence", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"id":42,"status":"under-review","currency":"NGN","createdAt":"2026-06-01T00:00:00Z","transaction":{"reference":"ref_42","amount":20000,"currency":"NGN"}}}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.SubmitEvidenceAsync("42", new DisputeEvidence
        {
            CustomerName = "Buyer",
            CustomerEmailAddress = "b@example.com",
            Explanation = "Goods were delivered."
        });
        Assert.Equal(DisputeStatus.UnderReview, dispute.Status);
    }

    [Fact]
    public async Task AcceptDisputeAsync_ReturnsAcceptedStatus()
    {
        var step = 0;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            step++;
            if (step == 1)
            {
                Assert.Equal(HttpMethod.Get, req.Method);
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                    {"status":true,"data":{"id":7,"status":"awaiting-merchant-feedback","currency":"NGN","refund_amount":10000,"createdAt":"2026-06-01T00:00:00Z","transaction":{"reference":"ref_7","amount":10000,"currency":"NGN"}}}
                    """);
            }
            Assert.Equal(HttpMethod.Put, req.Method);
            Assert.Contains("/resolve", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":true,"data":{"id":7,"status":"resolved","resolution":"merchant-accepted","currency":"NGN","createdAt":"2026-06-01T00:00:00Z","transaction":{"reference":"ref_7","amount":10000,"currency":"NGN"}}}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.AcceptDisputeAsync("7");
        Assert.Equal(DisputeStatus.Accepted, dispute.Status);
    }
}
