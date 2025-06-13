using System.Threading.Tasks;

using Bhengu.Finance.Payments.PayShap.Client;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Services.Implementations;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;

using Microsoft.Extensions.Logging;

using Moq;

using Xunit;

namespace Bhengu.Finance.Payments.PayShap.Tests.Services
{
    public class PayShapServiceTests
    {
        private readonly Mock<IPayShapClient> _mockClient = new();
        private readonly Mock<ILogger<PayShapService>> _mockLogger = new();

        private readonly IPayShapService _service;

        public PayShapServiceTests()
        {
            _service = new PayShapService(_mockClient.Object, _mockLogger.Object);
        }

        [Fact]
        public async Task InitiatePaymentAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SendInitiationAsync(It.IsAny<PaymentInitiationRequest>()))
                .ReturnsAsync(new PaymentInitiationResponse());

            var result = await _service.InitiatePaymentAsync(new PaymentInitiationRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task GetPaymentStatusAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SendStatusRequestAsync(It.IsAny<PaymentStatusRequest>()))
                .ReturnsAsync(new PaymentStatusResponse());

            var result = await _service.GetPaymentStatusAsync(new PaymentStatusRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task ConfirmPaymentAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SendConfirmationAsync(It.IsAny<PaymentConfirmationRequest>()))
                .ReturnsAsync(new PaymentConfirmationResponse());

            var result = await _service.ConfirmPaymentAsync(new PaymentConfirmationRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task VerifyCheckDigitAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SendCDVRequestAsync(It.IsAny<CheckDigitVerificationRequest>()))
                .ReturnsAsync(new CheckDigitVerificationResponse());

            var result = await _service.VerifyCheckDigitAsync(new CheckDigitVerificationRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task VerifyAccountAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SendAccountVerificationAsync(It.IsAny<AccountVerificationRequest>()))
                .ReturnsAsync(new AccountVerificationResponse());

            var result = await _service.VerifyAccountAsync(new AccountVerificationRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task ResolveProxyAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.ResolveProxyAsync(It.IsAny<ProxyResolutionRequest>()))
                .ReturnsAsync(new ProxyResolutionResponse());

            var result = await _service.ResolveProxyAsync(new ProxyResolutionRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task RegisterProxyAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.RegisterProxyAsync(It.IsAny<RegisterProxyRequest>()))
                .ReturnsAsync(new RegisterProxyResponse());

            var result = await _service.RegisterProxyAsync(new RegisterProxyRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task DeregisterProxyAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.DeregisterProxyAsync(It.IsAny<DeregisterProxyRequest>()))
                .ReturnsAsync(new DeregisterProxyResponse());

            var result = await _service.DeregisterProxyAsync(new DeregisterProxyRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task QueryProxyAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.QueryProxyAsync(It.IsAny<QueryProxyRequest>()))
                .ReturnsAsync(new QueryProxyResponse());

            var result = await _service.QueryProxyAsync(new QueryProxyRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task SubscribeToEventAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SubscribeToEventAsync(It.IsAny<SubscribeEventRequest>()))
                .ReturnsAsync(new SubscribeEventResponse());

            var result = await _service.SubscribeToEventAsync(new SubscribeEventRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task InitiateRtcPaymentAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.InitiateRtcPaymentAsync(It.IsAny<RtcPaymentRequest>()))
                .ReturnsAsync(new RtcPaymentResponse());

            var result = await _service.InitiateRtcPaymentAsync(new RtcPaymentRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task SendRtcAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SendRtcAsync(It.IsAny<SendRtcRequest>()))
                .ReturnsAsync(new SendRtcResponse());

            var result = await _service.SendRtcAsync(new SendRtcRequest());

            Assert.NotNull(result);
        }

        [Fact]
        public async Task SendEftAsync_ShouldReturnResponse()
        {
            _mockClient.Setup(c => c.SendEftAsync(It.IsAny<EFTPaymentRequest>()))
                .ReturnsAsync(new EFTPaymentResponse());

            var result = await _service.SendEftAsync(new EFTPaymentRequest());

            Assert.NotNull(result);
        }
    }
}
