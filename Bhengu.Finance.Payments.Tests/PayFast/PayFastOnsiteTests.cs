// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

/// <summary>PayFast Onsite (in-page popup) checkout — UUID generation, verified against the official SDK.</summary>
public class PayFastOnsiteTests
{
    private static PayFastOnsiteProvider Make(bool sandbox, string reply, List<string> capturedBodies, List<string>? capturedUris = null)
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            capturedBodies.Add(req.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty);
            capturedUris?.Add(req.RequestUri!.ToString());
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, reply);
        });
        var opts = Options.Create(new PayFastOptions { MerchantId = "10000100", MerchantKey = "k", Passphrase = "pp", UseSandbox = sandbox });
        var formBuilder = new PayFastFormBuilder(opts, NullLogger<PayFastFormBuilder>.Instance);
        return new PayFastOnsiteProvider(new HttpClient(handler), opts, formBuilder, NullLogger<PayFastOnsiteProvider>.Instance);
    }

    [Fact]
    public async Task GeneratePaymentIdentifierAsync_Production_PostsSignedBody_ReturnsUuid()
    {
        var bodies = new List<string>();
        var uris = new List<string>();
        var provider = Make(sandbox: false, reply: """{"uuid":"abc-123-uuid"}""", bodies, uris);

        var uuid = await provider.GeneratePaymentIdentifierAsync("pay-1", 100m, "Order #1");

        Assert.Equal("abc-123-uuid", uuid);
        Assert.Contains("onsite/process", Assert.Single(uris));
        var body = Assert.Single(bodies);
        Assert.Contains("amount=100.00", body);   // rands, not cents
        Assert.Contains("signature=", body);
    }

    [Fact]
    public async Task GeneratePaymentIdentifierAsync_Sandbox_Throws_NoNetworkCall()
    {
        var bodies = new List<string>();
        var provider = Make(sandbox: true, reply: "{}", bodies);

        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GeneratePaymentIdentifierAsync("pay-1", 100m, "Order"));
        Assert.Empty(bodies);
    }

    [Fact]
    public async Task GeneratePaymentIdentifierAsync_NoUuidInResponse_Throws()
    {
        var bodies = new List<string>();
        var provider = Make(sandbox: false, reply: """{"error":"bad"}""", bodies);

        await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GeneratePaymentIdentifierAsync("pay-1", 100m, "Order"));
    }
}
