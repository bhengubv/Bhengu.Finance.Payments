// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Tests.TestHelpers;
using Bhengu.Finance.Payments.UnionPay.Configuration;
using Bhengu.Finance.Payments.UnionPay.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.UnionPay;

public class UnionPayTypedWebhookTests
{
    private static readonly RSA SharedRsa = RSA.Create(2048);
    private static readonly string PrivateKeyPem = Convert.ToBase64String(SharedRsa.ExportPkcs8PrivateKey());
    private static readonly string PublicKeyPem = Convert.ToBase64String(SharedRsa.ExportSubjectPublicKeyInfo());

    private static UnionPayPaymentProvider Create() =>
        new(new HttpClient(new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))),
            Options.Create(new UnionPayOptions
            {
                MerId = "777290058110097",
                CertId = "68759585097",
                SignCertPrivateKey = PrivateKeyPem,
                VerifyCertPublicKey = PublicKeyPem,
                FrontUrl = "https://x/r",
                BackUrl = "https://x/b",
                Currency = "156",
                Encoding = "UTF-8",
                UseSandbox = true
            }),
            NullLogger<UnionPayPaymentProvider>.Instance);

    [Fact]
    public async Task ParseWebhook_Charge00_ReturnsChargeSucceededEvent()
    {
        var evt = await Create().ParseWebhookAsync(
            "queryId=Q1&orderId=O1&respCode=00&txnType=01&txnAmt=10000&currencyCode=156");
        var typed = Assert.IsType<ChargeSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
        Assert.Equal("156", typed.Currency);
    }

    [Fact]
    public async Task ParseWebhook_ChargeNon00_ReturnsChargeFailedEvent()
    {
        var evt = await Create().ParseWebhookAsync(
            "queryId=Q2&orderId=O2&respCode=99&respMsg=fail&txnType=01&txnAmt=5000&currencyCode=156");
        var typed = Assert.IsType<ChargeFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargeFailed, typed.Category);
        Assert.Equal("99", typed.FailureCode);
        Assert.Equal("fail", typed.FailureMessage);
    }

    [Fact]
    public async Task ParseWebhook_ChargePending03_ReturnsChargePendingEvent()
    {
        var evt = await Create().ParseWebhookAsync(
            "queryId=Q3&orderId=O3&respCode=03&txnType=01&txnAmt=2500&currencyCode=156");
        var typed = Assert.IsType<ChargePendingEvent>(evt);
        Assert.Equal(WebhookEventCategory.ChargePending, typed.Category);
        Assert.Equal(PaymentStatus.Pending, typed.Status);
    }

    [Fact]
    public async Task ParseWebhook_Refund00_ReturnsRefundSucceededEvent()
    {
        var evt = await Create().ParseWebhookAsync(
            "queryId=R1&orderId=RF1&respCode=00&txnType=04&txnAmt=10000&currencyCode=156");
        var typed = Assert.IsType<RefundSucceededEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundSucceeded, typed.Category);
        Assert.Equal(100m, typed.Amount);
    }

    [Fact]
    public async Task ParseWebhook_RefundNon00_ReturnsRefundFailedEvent()
    {
        var evt = await Create().ParseWebhookAsync(
            "queryId=R2&orderId=RF2&respCode=99&respMsg=fail&txnType=04&txnAmt=10000&currencyCode=156");
        var typed = Assert.IsType<RefundFailedEvent>(evt);
        Assert.Equal(WebhookEventCategory.RefundFailed, typed.Category);
    }
}
