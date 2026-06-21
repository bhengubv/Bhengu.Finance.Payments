// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.Interswitch;

/// <summary>
/// Verifies the Interswitch "InterswitchAuth" request-signature security headers match the scheme
/// documented at https://sandbox.interswitchng.com/docbase/docs/interswitch-sec-headers/sample-code/
/// and https://interswitch-docs.readme.io/reference/header-computation:
///   signature = Base64( SHA512( method &amp; percent_encode(absoluteUrl) &amp; timestampSeconds &amp; nonce &amp; clientId &amp; clientSecret ) )
///   timestamp = Unix SECONDS (NOT milliseconds); SignatureMethod header = "SHA512".
/// </summary>
public class InterswitchSignatureTests
{
    private const string TokenJson = """{"access_token":"isw-token-xyz","token_type":"bearer","expires_in":3600}""";
    private const string ClientId = "isw-client-id";
    private const string ClientSecret = "isw-client-secret";

    private static InterswitchOptions Opts() => new()
    {
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        MerchantCode = "MX12345",
        ProductId = "10101",
        WebhookSecret = "webhook-test-secret",
        UseSandbox = true
    };

    /// <summary>Recompute the expected signature exactly the way the verified Java sample does.</summary>
    private static string ExpectedSignature(string method, string absoluteUrl, string timestamp, string nonce)
    {
        var encodedUrl = Uri.EscapeDataString(absoluteUrl);
        var cipher = $"{method}&{encodedUrl}&{timestamp}&{nonce}&{ClientId}&{ClientSecret}";
        return Convert.ToBase64String(SHA512.HashData(Encoding.UTF8.GetBytes(cipher)));
    }

    [Fact]
    public async Task ProcessPayment_SignsRequest_WithBase64Sha512_SecondsTimestamp_AbsoluteUrl()
    {
        HttpRequestMessage? apiRequest = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.OrdinalIgnoreCase))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson);
            apiRequest = req;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"transactionRef":"ISW-TX-1","responseCode":"00","responseDescription":"Approved"}""");
        });

        var http = new HttpClient(handler);
        var provider = new InterswitchPaymentProvider(http, Options.Create(Opts()),
            NullLogger<InterswitchPaymentProvider>.Instance);

        await provider.ProcessPaymentAsync(new PaymentRequest
        {
            PaymentMethodToken = "TRF-1",
            Amount = 100m,
            Currency = "NGN",
            Description = "sig test"
        });

        Assert.NotNull(apiRequest);

        var signature = Assert.Single(apiRequest!.Headers.GetValues("Signature"));
        var method = Assert.Single(apiRequest.Headers.GetValues("SignatureMethod"));
        var timestamp = Assert.Single(apiRequest.Headers.GetValues("Timestamp"));
        var nonce = Assert.Single(apiRequest.Headers.GetValues("Nonce"));

        // SignatureMethod must be the literal "SHA512" (no hyphen).
        Assert.Equal("SHA512", method);

        // Timestamp must be Unix SECONDS, not milliseconds. A seconds value for 2026 is 10 digits;
        // a milliseconds value would be 13. Also assert it is within a sane window of "now".
        Assert.Equal(10, timestamp.Length);
        var ts = long.Parse(timestamp, CultureInfo.InvariantCulture);
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.InRange(ts, nowSeconds - 120, nowSeconds + 120);

        // Signature must be valid Base64 (not hex). Hex of a SHA-512 is 128 lowercase [0-9a-f] chars;
        // Base64 of 64 bytes is 88 chars ending in "==".
        var decoded = Convert.FromBase64String(signature); // throws if not Base64 → test fails
        Assert.Equal(64, decoded.Length);                  // SHA-512 digest is 64 bytes
        Assert.EndsWith("==", signature);

        // Reconstruct the absolute, percent-encoded resource URL and assert the exact signature value.
        // Sandbox base host is sandbox.interswitchng.com (verified), path is the advices resource.
        var absoluteUrl = apiRequest.RequestUri!.AbsoluteUri;
        Assert.StartsWith("https://sandbox.interswitchng.com/", absoluteUrl);
        Assert.Equal(ExpectedSignature("POST", absoluteUrl, timestamp, nonce), signature);
    }

    [Fact]
    public async Task TokenisationProvider_SignsRequest_WithSameVerifiedScheme()
    {
        // Exercises the shared InterswitchHttpClient signing path (used by the auxiliary providers).
        HttpRequestMessage? apiRequest = null;
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("oauth/token", StringComparison.OrdinalIgnoreCase))
                return StubHttpMessageHandler.Json(HttpStatusCode.OK, TokenJson);
            apiRequest = req;
            return StubHttpMessageHandler.Json(HttpStatusCode.OK,
                """{"cardToken":"CT-1","customerId":"cust-1","maskedPan":"512345******0008","expiryDate":"0530"}""");
        });

        var http = new HttpClient(handler);
        var provider = new InterswitchTokenisationProvider(http, Options.Create(Opts()),
            NullLogger<InterswitchTokenisationProvider>.Instance);

        _ = await provider.GetPaymentMethodAsync("CT-1");

        Assert.NotNull(apiRequest);
        var signature = Assert.Single(apiRequest!.Headers.GetValues("Signature"));
        var method = Assert.Single(apiRequest.Headers.GetValues("SignatureMethod"));
        var timestamp = Assert.Single(apiRequest.Headers.GetValues("Timestamp"));
        var nonce = Assert.Single(apiRequest.Headers.GetValues("Nonce"));

        Assert.Equal("SHA512", method);
        Assert.Equal(10, timestamp.Length);
        Assert.Equal(64, Convert.FromBase64String(signature).Length);

        var absoluteUrl = apiRequest.RequestUri!.AbsoluteUri;
        Assert.StartsWith("https://sandbox.interswitchng.com/", absoluteUrl);
        Assert.Equal(ExpectedSignature("GET", absoluteUrl, timestamp, nonce), signature);
    }
}
