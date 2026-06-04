// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.UnionPay;

public class UnionPaySettlementProviderTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string PrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());

    private static UnionPaySettlementProvider Create(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new UnionPayOptions
            {
                MerId = "777290058110097",
                CertId = "68759585097",
                SignCertPrivateKey = PrivateKeyPem,
                Currency = "156",
                Encoding = "UTF-8",
                UseSandbox = true
            }),
            NullLogger<UnionPaySettlementProvider>.Instance);

    [Fact]
    public async Task ListSettlementsAsync_PerDay_ReturnsSettlement()
    {
        var handler = new StubHttpMessageHandler((req, _) =>
        {
            Assert.Contains("fileTransfer.do", req.RequestUri!.PathAndQuery);
            return StubHttpMessageHandler.Text(HttpStatusCode.OK,
                "respCode=00&batchNo=BATCH20260501&settleAmt=100050&totalAmt=105050&totalQty=10&settleAcct=12345678");
        });
        var provider = Create(handler);

        var list = await provider.ListSettlementsAsync(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)).ToListAsync();

        Assert.Single(list);
        Assert.Equal("BATCH20260501", list[0].Reference);
        Assert.Equal(1000.50m, list[0].NetAmount);
        Assert.Equal(1050.50m, list[0].GrossAmount);
        Assert.Equal(10, list[0].TransactionCount);
    }

    [Fact]
    public async Task ListSettlementsAsync_RespCodeNonZero_Skipped()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.OK, "respCode=99&respMsg=No file"));
        var provider = Create(handler);
        var list = await provider.ListSettlementsAsync(
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)).ToListAsync();
        Assert.Empty(list);
    }

    [Fact]
    public async Task GetSettlementAsync_DerivesDate_FromTrailingDigits()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.OK,
                "respCode=00&batchNo=BATCH20260501&settleAmt=100050&totalAmt=105050&totalQty=10&settleAcct=12345678"));
        var provider = Create(handler);
        var settlement = await provider.GetSettlementAsync("BATCH20260501");
        Assert.NotNull(settlement);
        Assert.Equal("BATCH20260501", settlement!.Reference);
    }

    [Fact]
    public async Task ListTransactionsAsync_ParsesBreakdownCsv()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            StubHttpMessageHandler.Text(HttpStatusCode.OK,
                "respCode=00&breakDownInfo=tx1,01,100,1,156,20260501120000%0Atx2,04,5000,10,156,20260501130000"));
        var provider = Create(handler);
        var list = await provider.ListTransactionsAsync("BATCH20260501").ToListAsync();
        Assert.Equal(2, list.Count);
        Assert.Equal(SettlementTransactionKind.Charge, list[0].Kind);
        Assert.Equal(SettlementTransactionKind.Refund, list[1].Kind);
        Assert.Equal(0.99m, list[0].NetAmount);
    }

    [Fact]
    public async Task ListSettlementsAsync_OnNetworkFailure_Throws()
    {
        var handler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("dns"));
        var provider = Create(handler);
        await Assert.ThrowsAsync<ProviderUnavailableException>(async () => await provider.ListSettlementsAsync(DateTime.UtcNow, DateTime.UtcNow).ToListAsync());
    }
}
