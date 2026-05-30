// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Providers;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;
using Bhengu.Finance.Payments.PayShap.Utilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayShap;

public class PayShapPaymentProviderTests
{
    private static PayShapPaymentProvider CreateProvider(
        out Mock<IPayShapService> serviceMock,
        PayShapSettings? settings = null)
    {
        settings ??= new PayShapSettings { SignatureKey = "test-signing-secret" };
        serviceMock = new Mock<IPayShapService>();
        return new PayShapPaymentProvider(
            serviceMock.Object,
            Options.Create(settings),
            NullLogger<PayShapPaymentProvider>.Instance);
    }

    private static PaymentRequest FullRequest(Dictionary<string, string>? extraMeta = null)
    {
        var meta = new Dictionary<string, string>
        {
            ["payshap.payer.account"] = "1234567890",
            ["payshap.payer.bank_code"] = "FNB",
            ["payshap.payer.name"] = "Payer Co",
            ["payshap.payee.account"] = "9876543210",
            ["payshap.payee.bank_code"] = "ABSA",
            ["payshap.payee.name"] = "Payee Co"
        };
        if (extraMeta is not null)
            foreach (var (k, v) in extraMeta) meta[k] = v;

        return new PaymentRequest
        {
            PaymentMethodToken = "0821234567",
            Amount = 250m,
            Currency = "ZAR",
            Description = "Test transfer",
            Metadata = meta
        };
    }

    [Fact]
    public async Task ProcessPaymentAsync_Throws_WhenRequiredMetadataMissing()
    {
        var provider = CreateProvider(out _);
        var minimal = new PaymentRequest
        {
            PaymentMethodToken = "0821234567",
            Amount = 100m,
            Currency = "ZAR",
            Description = "Missing payer details"
        };

        var ex = await Assert.ThrowsAsync<PaymentDeclinedException>(() => provider.ProcessPaymentAsync(minimal));
        Assert.Equal("missing_metadata", ex.ProviderErrorCode);
        Assert.Contains("payshap.payer.account", ex.ProviderErrorMessage);
    }

    [Fact]
    public async Task ProcessPaymentAsync_CallsInitiateRtcPayment_WithMappedFields()
    {
        var provider = CreateProvider(out var serviceMock);
        serviceMock.Setup(s => s.InitiateRtcPaymentAsync(It.IsAny<RtcPaymentRequest>()))
            .ReturnsAsync(new RtcPaymentResponse
            {
                TransactionId = "RTC-TX-1",
                Status = "COMPLETED",
                ConfirmationMessage = "Settled",
                Timestamp = "2026-05-30T12:00:00Z"
            });

        var response = await provider.ProcessPaymentAsync(FullRequest());

        Assert.Equal("RTC-TX-1", response.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, response.Status);
        Assert.Equal(250m, response.Amount);
        Assert.Equal("ZAR", response.Currency);
        Assert.Equal("Settled", response.Message);

        serviceMock.Verify(s => s.InitiateRtcPaymentAsync(It.Is<RtcPaymentRequest>(r =>
            r.Amount == "250.00" &&
            r.Currency == "ZAR" &&
            r.Initiator.AccountNumber == "1234567890" &&
            r.Initiator.BankCode == "FNB" &&
            r.Recipient.AccountNumber == "9876543210" &&
            r.Recipient.IdentifierValue == "0821234567"
        )), Times.Once);
    }

    [Fact]
    public async Task ProcessPaymentAsync_PrefersExplicitIdentifierValueOverPaymentMethodToken()
    {
        var provider = CreateProvider(out var serviceMock);
        serviceMock.Setup(s => s.InitiateRtcPaymentAsync(It.IsAny<RtcPaymentRequest>()))
            .ReturnsAsync(new RtcPaymentResponse { TransactionId = "X", Status = "PENDING" });

        var request = FullRequest(new Dictionary<string, string>
        {
            ["payshap.payee.identifier_type"] = "MSISDN",
            ["payshap.payee.identifier_value"] = "0834567890"
        });

        await provider.ProcessPaymentAsync(request);

        serviceMock.Verify(s => s.InitiateRtcPaymentAsync(It.Is<RtcPaymentRequest>(r =>
            r.Recipient.IdentifierType == "MSISDN" &&
            r.Recipient.IdentifierValue == "0834567890"
        )), Times.Once);
    }

    [Fact]
    public async Task ProcessPaymentAsync_WrapsHttpRequestExceptionAsProviderUnavailableException()
    {
        var provider = CreateProvider(out var serviceMock);
        serviceMock.Setup(s => s.InitiateRtcPaymentAsync(It.IsAny<RtcPaymentRequest>()))
            .ThrowsAsync(new HttpRequestException("DNS fail"));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.ProcessPaymentAsync(FullRequest()));
    }

    [Fact]
    public async Task ProcessRefundAsync_Throws_BecausePayShapHasNoRefundConcept()
    {
        var provider = CreateProvider(out _);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.ProcessRefundAsync(new RefundRequest
            {
                GatewayReference = "RTC-TX-1",
                Amount = 100m,
                Reason = "Customer wanted"
            }));
        Assert.Contains("no refund API", ex.Message);
        Assert.Contains("RTC payment with payer and payee swapped", ex.Message);
    }

    [Fact]
    public void VerifyWebhookSignature_Throws_WhenSignatureKeyMissing()
    {
        var provider = CreateProvider(out _, new PayShapSettings { SignatureKey = "" });
        Assert.Throws<ProviderConfigurationException>(() =>
            provider.VerifyWebhookSignature("payload", "any"));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsTrue_ForValidSignature()
    {
        const string secret = "test-signing-secret";
        const string payload = """{"event_id":"E1","event_type":"payment.completed"}""";
        var validSig = PayShapSignatureHelper.GenerateSignature(payload, secret);

        var provider = CreateProvider(out _, new PayShapSettings { SignatureKey = secret });
        Assert.True(provider.VerifyWebhookSignature(payload, validSig));
    }

    [Fact]
    public void VerifyWebhookSignature_ReturnsFalse_ForTamperedSignature()
    {
        var provider = CreateProvider(out _);
        Assert.False(provider.VerifyWebhookSignature("anything", "deadbeef"));
    }

    [Fact]
    public async Task ParseWebhookAsync_DeserialisesAndMapsStatus()
    {
        var provider = CreateProvider(out _);
        var payload = """
            {
              "event_id": "E-1",
              "event_type": "payment.completed",
              "created_at": "2026-05-30T12:00:00Z",
              "data": {
                "transaction_id": "RTC-TX-99",
                "status": "COMPLETED",
                "reference": "ref-1",
                "amount": 250.00,
                "currency": "ZAR",
                "sender_account": "1234",
                "receiver_account": "5678"
              }
            }
            """;

        var evt = await provider.ParseWebhookAsync(payload);

        Assert.NotNull(evt);
        Assert.Equal("RTC-TX-99", evt!.GatewayReference);
        Assert.Equal(PaymentStatus.Completed, evt.Status);
        Assert.Equal("payment.completed", evt.EventType);
        Assert.Equal("ref-1", evt.RawPayload!["reference"]);
    }

    [Fact]
    public async Task ParseWebhookAsync_ReturnsNull_ForInvalidJson()
    {
        var provider = CreateProvider(out _);
        var evt = await provider.ParseWebhookAsync("not json");
        Assert.Null(evt);
    }
}
