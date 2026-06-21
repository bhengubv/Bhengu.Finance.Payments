// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Providers;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Live BRICS Pay smoke test. Skipped by default — running it requires a provisioned terminal
/// (TerminalId, the per-terminal BaseUrl, and a private key whose public half is registered with the
/// processor). See BRICS_PAY_API_REFERENCE.md.
/// </summary>
public class BricsPayIntegrationTests
{
    private static BricsPayPaymentProvider CreateProvider()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var opts = new BricsPayOptions
        {
            TerminalId = "POS-TEST",
            BaseUrl = "https://terminal.brics.example",
            PrivateKeyPem = ec.ExportPkcs8PrivateKeyPem()
        };
        return new BricsPayPaymentProvider(new HttpClient(), Options.Create(opts), NullLogger<BricsPayPaymentProvider>.Instance);
    }

    [Fact(Skip = "Requires a provisioned BRICS Pay terminal (TerminalId, per-terminal BaseUrl, registered public key).")]
    public async Task ShouldCreateTransactionAgainstRealTerminal()
    {
        var provider = CreateProvider();
        var qr = await provider.GenerateQrAsync(new QrCodeRequest
        {
            Amount = 123.45m,
            Currency = "ZAR",
            Description = "Integration Test",
            MerchantReference = "IT-" + Guid.NewGuid().ToString("N"),
            PayerIdentifier = "test-user-hash"
        });

        Assert.NotNull(qr.Payload);
    }
}
