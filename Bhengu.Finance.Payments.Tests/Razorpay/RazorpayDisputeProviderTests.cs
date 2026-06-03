// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Bhengu.Finance.Payments.Razorpay.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Razorpay;

public class RazorpayDisputeProviderTests
{
    private static RazorpayDisputeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new RazorpayOptions { KeyId = "rzp_test_x", KeySecret = "secret_x" }),
            NullLogger<RazorpayDisputeProvider>.Instance);

    [Fact]
    public async Task GetDisputeAsync_DeserialisesSingle()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/disputes/disp_1", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"disp_1","entity":"dispute","payment_id":"pay_1","amount":100000,"currency":"INR","reason_code":"fraudulent","status":"open","respond_by":1700100000,"created_at":1700000000}""");
        });
        var provider = Create(handler);
        var dispute = await provider.GetDisputeAsync("disp_1");

        Assert.NotNull(dispute);
        Assert.Equal("disp_1", dispute!.Reference);
        Assert.Equal("pay_1", dispute.ChargeReference);
        Assert.Equal(1000m, dispute.Amount);
        Assert.Equal(DisputeStatus.NeedsResponse, dispute.Status);
        Assert.NotNull(dispute.EvidenceDueBy);
    }

    [Fact]
    public async Task GetDisputeAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetDisputeAsync("disp_missing"));
    }

    [Fact]
    public async Task ListDisputesAsync_DeserialisesCollection()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v1/disputes", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"entity":"collection","count":2,"items":[
                  {"id":"disp_1","entity":"dispute","payment_id":"pay_1","amount":100000,"currency":"INR","status":"open","created_at":1700000000},
                  {"id":"disp_2","entity":"dispute","payment_id":"pay_2","amount":200000,"currency":"INR","status":"won","created_at":1700003600}
                ]}
                """);
        });
        var provider = Create(handler);
        var disputes = await provider.ListDisputesAsync();

        Assert.Equal(2, disputes.Count);
        Assert.Equal(DisputeStatus.NeedsResponse, disputes[0].Status);
        Assert.Equal(DisputeStatus.Won, disputes[1].Status);
    }

    [Fact]
    public async Task ListDisputesAsync_PassesDateBounds()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("from=", req.RequestUri!.PathAndQuery);
            Assert.Contains("to=", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"entity":"collection","count":0,"items":[]}""");
        });
        var provider = Create(handler);
        var disputes = await provider.ListDisputesAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc));
        Assert.Empty(disputes);
    }

    [Fact]
    public async Task SubmitEvidenceAsync_PatchesContestEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Patch, req.Method);
            Assert.Contains("/contest", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"disp_1","entity":"dispute","payment_id":"pay_1","amount":100000,"currency":"INR","status":"under_review","created_at":1700000000}""");
        });
        var provider = Create(handler);
        var dispute = await provider.SubmitEvidenceAsync("disp_1", new DisputeEvidence
        {
            Explanation = "Shipped on time",
            ShippingTrackingNumber = "TRACK123"
        });
        Assert.Equal(DisputeStatus.UnderReview, dispute.Status);
    }

    [Fact]
    public async Task AcceptDisputeAsync_PostsAcceptEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/accept", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"id":"disp_1","entity":"dispute","payment_id":"pay_1","amount":100000,"currency":"INR","status":"accepted","created_at":1700000000}""");
        });
        var provider = Create(handler);
        var dispute = await provider.AcceptDisputeAsync("disp_1");
        Assert.Equal(DisputeStatus.Accepted, dispute.Status);
    }

    [Fact]
    public async Task GetDisputeAsync_OnRateLimit_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttled"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GetDisputeAsync("disp_1"));
    }

    [Fact]
    public async Task GetDisputeAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GetDisputeAsync("disp_1"));
    }
}
