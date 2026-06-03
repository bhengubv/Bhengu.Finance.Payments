// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Providers;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.PayShap;

public class PayShapQrCodeProviderTests
{
    private static PayShapSettings Defaults() => new()
    {
        MerchantId = "MERCHANT-123",
        SignatureKey = "secret",
        Payee = new PayShapPayeeSettings
        {
            BankCode = "250655",
            Account = "9876543210",
            Name = "Geek Coffee",
            IdentifierType = "MSISDN",
            IdentifierValue = "+27821234567"
        }
    };

    private static PayShapQrCodeProvider CreateProvider(
        out Mock<IPayShapService> serviceMock,
        PayShapSettings? settings = null)
    {
        serviceMock = new Mock<IPayShapService>();
        return new PayShapQrCodeProvider(
            serviceMock.Object,
            Options.Create(settings ?? Defaults()),
            NullLogger<PayShapQrCodeProvider>.Instance);
    }

    [Fact]
    public void ProviderName_IsPayShap()
    {
        var provider = CreateProvider(out _);
        Assert.Equal("payshap", provider.ProviderName);
    }

    [Fact]
    public async Task GenerateQrAsync_ProducesDynamicPayload_WithAmount()
    {
        var provider = CreateProvider(out _);
        var qr = await provider.GenerateQrAsync(new QrCodeRequest
        {
            Amount = 49.99m,
            Currency = "ZAR",
            Description = "Latte",
            MerchantReference = "order-1",
            Format = QrFormat.Payload
        });

        Assert.Equal("order-1", qr.Reference);
        Assert.Equal(QrFormat.Payload, qr.Format);
        Assert.Equal(49.99m, qr.Amount);
        Assert.Equal("ZAR", qr.Currency);
        Assert.NotNull(qr.Payload);
        Assert.StartsWith("payshap://pay?", qr.Payload);
        Assert.Contains("amount=49.99", qr.Payload);
        Assert.Contains("currency=ZAR", qr.Payload);
        Assert.Contains("bankcode=250655", qr.Payload);
        Assert.Contains("account=9876543210", qr.Payload);
        Assert.Contains("ref=order-1", qr.Payload);
        Assert.Contains("payeename=Geek%20Coffee", qr.Payload);
        Assert.Contains("identifiertype=MSISDN", qr.Payload);
    }

    [Fact]
    public async Task GenerateQrAsync_ProducesStaticPayload_WithoutAmount()
    {
        var provider = CreateProvider(out _);
        var qr = await provider.GenerateQrAsync(new QrCodeRequest
        {
            Amount = null,
            Currency = "ZAR",
            Description = "Tip jar",
            MerchantReference = "tipjar-1",
            Format = QrFormat.Payload
        });

        Assert.Null(qr.Amount);
        Assert.NotNull(qr.Payload);
        Assert.DoesNotContain("amount=", qr.Payload);
        Assert.DoesNotContain("currency=", qr.Payload);
        Assert.Contains("ref=tipjar-1", qr.Payload);
    }

    [Fact]
    public async Task GenerateQrAsync_ThrowsForPngFormat()
    {
        var provider = CreateProvider(out _);
        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.GenerateQrAsync(new QrCodeRequest
            {
                Currency = "ZAR",
                Description = "x",
                MerchantReference = "r-1",
                Format = QrFormat.Png
            }));
        Assert.Contains("QrFormat.Png not supported", ex.Message);
        Assert.Contains("QRCoder", ex.Message);
    }

    [Fact]
    public async Task GenerateQrAsync_ThrowsForSvgFormat()
    {
        var provider = CreateProvider(out _);
        await Assert.ThrowsAsync<BhenguPaymentException>(() =>
            provider.GenerateQrAsync(new QrCodeRequest
            {
                Currency = "ZAR",
                Description = "x",
                MerchantReference = "r-1",
                Format = QrFormat.Svg
            }));
    }

    [Fact]
    public async Task GenerateQrAsync_ThrowsConfigurationException_WhenPayeeNotConfigured()
    {
        var provider = CreateProvider(out _, new PayShapSettings { MerchantId = "M" });
        await Assert.ThrowsAsync<ProviderConfigurationException>(() =>
            provider.GenerateQrAsync(new QrCodeRequest
            {
                Currency = "ZAR",
                Description = "x",
                MerchantReference = "r-1",
                Format = QrFormat.Payload
            }));
    }

    [Theory]
    [InlineData("COMPLETED", PaymentStatus.Completed)]
    [InlineData("SUCCESS", PaymentStatus.Completed)]
    [InlineData("SETTLED", PaymentStatus.Completed)]
    [InlineData("PENDING", PaymentStatus.Pending)]
    [InlineData("PROCESSING", PaymentStatus.Pending)]
    [InlineData("FAILED", PaymentStatus.Failed)]
    [InlineData("CANCELLED", PaymentStatus.Cancelled)]
    [InlineData("EXPIRED", PaymentStatus.Cancelled)]
    [InlineData("REVERSED", PaymentStatus.Refunded)]
    [InlineData("UNKNOWN_STATUS", PaymentStatus.Pending)]
    public async Task GetQrStatusAsync_MapsAllStates(string raw, PaymentStatus expected)
    {
        var provider = CreateProvider(out var serviceMock);
        serviceMock.Setup(s => s.GetPaymentStatusAsync(It.IsAny<PaymentStatusRequest>()))
            .ReturnsAsync(new PaymentStatusResponse { TransactionId = "ref-x", Status = raw });

        var status = await provider.GetQrStatusAsync("ref-x");

        Assert.Equal(expected, status);
        serviceMock.Verify(s => s.GetPaymentStatusAsync(It.Is<PaymentStatusRequest>(r =>
            r.PaymentId == "ref-x" && r.MerchantId == "MERCHANT-123")), Times.Once);
    }

    [Fact]
    public async Task GetQrStatusAsync_WrapsHttpRequestExceptionAsProviderUnavailable()
    {
        var provider = CreateProvider(out var serviceMock);
        serviceMock.Setup(s => s.GetPaymentStatusAsync(It.IsAny<PaymentStatusRequest>()))
            .ThrowsAsync(new HttpRequestException("network down"));

        await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GetQrStatusAsync("ref-x"));
    }

    [Fact]
    public async Task GetQrStatusAsync_WrapsGenericExceptionAsBhenguPaymentException()
    {
        var provider = CreateProvider(out var serviceMock);
        serviceMock.Setup(s => s.GetPaymentStatusAsync(It.IsAny<PaymentStatusRequest>()))
            .ThrowsAsync(new InvalidOperationException("upstream broke"));

        var ex = await Assert.ThrowsAsync<BhenguPaymentException>(() => provider.GetQrStatusAsync("ref-x"));
        Assert.Contains("status lookup failed", ex.Message);
    }
}
