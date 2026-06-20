// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Bhengu.Finance.Payments.PayFast.Providers;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayFast;

/// <summary>
/// Tests for the four-gate <see cref="PayFastPaymentProvider.ValidateItnAsync"/> ITN validator:
/// signature, source IP, PayFast server confirmation, and amount reconciliation.
/// </summary>
public class PayFastItnValidationTests
{
    private const string Passphrase = "test-pass-phrase";

    /// <summary>Build a PayFast ITN body whose signature the provider will accept (alphabetical-sort MD5 + passphrase).</summary>
    private static string SignedItn(
        string status = "COMPLETE",
        string amountGross = "100.00",
        string mPaymentId = "11111111-1111-1111-1111-111111111111",
        string pfPaymentId = "PF-TEST-1")
    {
        // Simple values only (no chars that change under URL-encode) so the canonical string is stable.
        var fields = new Dictionary<string, string>
        {
            ["m_payment_id"] = mPaymentId,
            ["pf_payment_id"] = pfPaymentId,
            ["payment_status"] = status,
            ["item_name"] = "Order1",
            ["amount_gross"] = amountGross,
        };

        var canonical = string.Join("&", fields
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={WebUtility.UrlEncode(p.Value)}")
            .Append($"passphrase={WebUtility.UrlEncode(Passphrase)}"));
        var signature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

        return string.Join("&", fields.Select(p => $"{p.Key}={p.Value}").Append($"signature={signature}"));
    }

    private static PayFastPaymentProvider ProviderReturning(string validateReply, HttpStatusCode code = HttpStatusCode.OK)
    {
        var handler = new StubHttpMessageHandler((_, _) => StubHttpMessageHandler.Text(code, validateReply));
        var http = new HttpClient(handler);
        return new PayFastPaymentProvider(
            http,
            Options.Create(new PayFastOptions { MerchantId = "10000100", Passphrase = Passphrase, UseSandbox = true }),
            NullLogger<PayFastPaymentProvider>.Instance);
    }

    [Fact]
    public async Task ValidateItnAsync_GoodSignatureAndServerConfirms_ReturnsValid()
    {
        var provider = ProviderReturning("VALID");

        var result = await provider.ValidateItnAsync(SignedItn());

        Assert.True(result.IsValid);
        Assert.True(result.SignatureValid);
        Assert.True(result.ServerConfirmed);
        Assert.Null(result.SourceValid);     // no source IP supplied -> gate skipped
        Assert.Null(result.AmountMatched);   // no expected amount -> gate skipped
        Assert.Equal("PF-TEST-1", result.PfPaymentId);
        Assert.Equal("COMPLETE", result.PaymentStatus);
        Assert.Equal(100.00m, result.AmountGross);
    }

    [Fact]
    public async Task ValidateItnAsync_TamperedSignature_FailsAtGate1_NoServerCall()
    {
        // If gate 1 ran the server call, this stub would wrongly confirm — proves we stop at the signature.
        var provider = ProviderReturning("VALID");
        var body = SignedItn();
        var tampered = body[..body.LastIndexOf("signature=", StringComparison.Ordinal)] + "signature=deadbeefdeadbeefdeadbeefdeadbeef";

        var result = await provider.ValidateItnAsync(tampered);

        Assert.False(result.IsValid);
        Assert.False(result.SignatureValid);
        Assert.False(result.ServerConfirmed);
        Assert.Contains("signature", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateItnAsync_PayFastReturnsInvalid_FailsAtGate3()
    {
        var provider = ProviderReturning("INVALID");

        var result = await provider.ValidateItnAsync(SignedItn());

        Assert.False(result.IsValid);
        Assert.True(result.SignatureValid);       // signature was fine...
        Assert.False(result.ServerConfirmed);     // ...but PayFast did not confirm
    }

    [Fact]
    public async Task ValidateItnAsync_AmountMismatch_FailsAtGate4()
    {
        var provider = ProviderReturning("VALID");

        var result = await provider.ValidateItnAsync(SignedItn(amountGross: "100.00"), expectedAmount: 50.00m);

        Assert.False(result.IsValid);
        Assert.True(result.ServerConfirmed);
        Assert.False(result.AmountMatched);
    }

    [Fact]
    public async Task ValidateItnAsync_AmountMatches_ReturnsValid()
    {
        var provider = ProviderReturning("VALID");

        var result = await provider.ValidateItnAsync(SignedItn(amountGross: "100.00"), expectedAmount: 100m);

        Assert.True(result.IsValid);
        Assert.True(result.AmountMatched);
    }

    [Fact]
    public async Task ValidateItnAsync_NonPayFastSourceIp_FailsAtGate2_NoServerCall()
    {
        // "VALID" stub would confirm if we reached gate 3 — proves we reject at the source gate first.
        var provider = ProviderReturning("VALID");

        var result = await provider.ValidateItnAsync(SignedItn(), sourceIp: "not-an-ip-address");

        Assert.False(result.IsValid);
        Assert.True(result.SignatureValid);
        Assert.False(result.SourceValid);
        Assert.False(result.ServerConfirmed);
    }
}
