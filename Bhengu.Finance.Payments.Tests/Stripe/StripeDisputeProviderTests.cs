// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

[Collection(StripeConfigurationCollection.Name)]
public class StripeDisputeProviderTests
{
    private static StripeDisputeProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new StripeOptions { SecretKey = "sk_test_fake", WebhookSecret = "whsec_test_fake" }),
            NullLogger<StripeDisputeProvider>.Instance);

    [Fact]
    public async Task GetDisputeAsync_ReturnsMappedDispute()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("disputes/dp_test", req.RequestUri!.PathAndQuery, StringComparison.Ordinal);
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"dp_test","object":"dispute","amount":12345,"currency":"usd","charge":"ch_x","payment_intent":"pi_x","status":"needs_response","reason":"fraudulent","created":1700000000,"evidence_details":{"due_by":1702592000,"has_evidence":false,"past_due":false,"submission_count":0}}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.GetDisputeAsync("dp_test");

        Assert.NotNull(dispute);
        Assert.Equal("dp_test", dispute!.Reference);
        Assert.Equal("ch_x", dispute.ChargeReference);
        Assert.Equal(123.45m, dispute.Amount);
        Assert.Equal("USD", dispute.Currency);
        Assert.Equal(DisputeStatus.NeedsResponse, dispute.Status);
        Assert.Equal("fraudulent", dispute.ReasonCode);
        Assert.NotNull(dispute.EvidenceDueBy);
    }

    [Fact]
    public async Task GetDisputeAsync_ReturnsNull_OnNotFound()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.NotFound, """
            {"error":{"type":"invalid_request_error","message":"No such dispute"}}
            """));
        var provider = Create(handler);
        Assert.Null(await provider.GetDisputeAsync("dp_missing"));
    }

    [Fact]
    public async Task ListDisputesAsync_ReturnsCollection()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.OK, """
            {"object":"list","data":[
                {"id":"dp_1","object":"dispute","amount":1000,"currency":"usd","charge":"ch_a","status":"won","reason":"duplicate","created":1700000000},
                {"id":"dp_2","object":"dispute","amount":2000,"currency":"usd","charge":"ch_b","status":"lost","reason":"fraudulent","created":1700100000}
            ],"has_more":false}
            """));
        var provider = Create(handler);
        var disputes = await provider.ListDisputesAsync().ToListAsync();
        Assert.Equal(2, disputes.Count);
        Assert.Equal(DisputeStatus.Won, disputes[0].Status);
        Assert.Equal(DisputeStatus.Lost, disputes[1].Status);
    }

    [Fact]
    public async Task SubmitEvidenceAsync_PostsEvidenceAndReturnsDispute()
    {
        string? bodyText = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.Method == HttpMethod.Post && req.Content is not null)
            {
                bodyText = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"dp_test","object":"dispute","amount":12345,"currency":"usd","charge":"ch_x","status":"under_review","reason":"fraudulent","created":1700000000}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.SubmitEvidenceAsync("dp_test", new DisputeEvidence
        {
            Explanation = "Customer ordered and received goods",
            CustomerName = "Jane Doe",
            ShippingCarrier = "DHL",
            ShippingTrackingNumber = "JD1234"
        });

        Assert.NotNull(bodyText);
        Assert.Contains("evidence", bodyText!, StringComparison.Ordinal);
        Assert.Equal(DisputeStatus.UnderReview, dispute.Status);
    }

    [Fact]
    public async Task SubmitEvidenceAsync_Throws4xxAsPaymentDeclined()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadRequest, """
            {"error":{"type":"invalid_request_error","code":"dispute_already_resolved","message":"Evidence window closed"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.SubmitEvidenceAsync("dp_late", new DisputeEvidence
        {
            Explanation = "too late"
        }));
    }

    [Fact]
    public async Task AcceptDisputeAsync_PostsToCloseEndpoint()
    {
        var sawClose = false;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/close", StringComparison.Ordinal)) sawClose = true;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, """
                {"id":"dp_test","object":"dispute","amount":12345,"currency":"usd","charge":"ch_x","status":"charge_refunded","reason":"fraudulent","created":1700000000}
                """);
        });
        var provider = Create(handler);
        var dispute = await provider.AcceptDisputeAsync("dp_test");
        Assert.True(sawClose);
        Assert.Equal(DisputeStatus.Accepted, dispute.Status);
    }

    [Fact]
    public async Task ListDisputesAsync_Throws429AsRateLimit()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.TooManyRequests, """
            {"error":{"type":"invalid_request_error","message":"Too many requests"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderRateLimitException>(async () => await provider.ListDisputesAsync().ToListAsync());
    }

    [Fact]
    public async Task ListDisputesAsync_Throws5xxAsProviderUnavailable()
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Json(HttpStatusCode.BadGateway, """
            {"error":{"type":"api_error","message":"Stripe is down"}}
            """));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () => await provider.ListDisputesAsync().ToListAsync());
    }
}
