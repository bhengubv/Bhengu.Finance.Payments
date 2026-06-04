// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Bhengu.Finance.Payments.Flutterwave.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Flutterwave;

public class FlutterwaveDisputeProviderTests
{
    private static FlutterwaveDisputeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new FlutterwaveOptions { SecretKey = "FLWSECK_TEST-x" }),
            NullLogger<FlutterwaveDisputeProvider>.Instance);

    [Fact]
    public async Task GetDisputeAsync_DeserialisesSingle()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/chargebacks/123", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":123,"transaction_id":4567,"amount":1000,"currency":"NGN","reason":"fraudulent","reason_description":"Customer alleges fraud","status":"pending","due_date":"2026-06-15T00:00:00Z","created_at":"2026-06-01T00:00:00Z","chargeback_fee":25}}
                """);
        });
        var provider = Create(handler);

        var dispute = await provider.GetDisputeAsync("123");
        Assert.NotNull(dispute);
        Assert.Equal("123", dispute!.Reference);
        Assert.Equal("4567", dispute.ChargeReference);
        Assert.Equal(1000m, dispute.Amount);
        Assert.Equal(DisputeStatus.NeedsResponse, dispute.Status);
        Assert.Equal("fraudulent", dispute.ReasonCode);
        Assert.NotNull(dispute.EvidenceDueBy);
        Assert.Equal(25m, dispute.ChargebackFee);
    }

    [Fact]
    public async Task GetDisputeAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.NotFound, "missing"));
        var provider = Create(handler);
        Assert.Null(await provider.GetDisputeAsync("999"));
    }

    [Fact]
    public async Task ListDisputesAsync_DeserialisesCollection()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("v3/chargebacks", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":[
                  {"id":1,"transaction_id":1001,"amount":100,"currency":"NGN","status":"pending","created_at":"2026-06-01T00:00:00Z"},
                  {"id":2,"transaction_id":1002,"amount":200,"currency":"NGN","status":"won","created_at":"2026-06-02T00:00:00Z"}
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
            Assert.Contains("to=", req.RequestUri.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """{"status":"success","data":[]}""");
        });
        var provider = Create(handler);
        await provider.ListDisputesAsync(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task SubmitEvidenceAsync_PostsContestEndpoint()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/contest", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":123,"transaction_id":4567,"amount":1000,"currency":"NGN","status":"under_review","created_at":"2026-06-01T00:00:00Z"}}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.SubmitEvidenceAsync("123", new DisputeEvidence
        {
            Explanation = "Goods shipped on time",
            ShippingTrackingNumber = "TRACK1"
        });
        Assert.Equal(DisputeStatus.UnderReview, dispute.Status);
    }

    [Fact]
    public async Task AcceptDisputeAsync_PostsAcceptEndpoint_AndReturnsAccepted()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Equal(HttpMethod.Post, req.Method);
            Assert.Contains("/accept", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"status":"success","data":{"id":123,"transaction_id":4567,"amount":1000,"currency":"NGN","status":"accepted","created_at":"2026-06-01T00:00:00Z"}}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.AcceptDisputeAsync("123");
        Assert.Equal(DisputeStatus.Accepted, dispute.Status);
    }

    [Fact]
    public async Task GetDisputeAsync_OnRateLimit_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(HttpStatusCode.TooManyRequests, "throttle"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(() => provider.GetDisputeAsync("123"));
    }
}
