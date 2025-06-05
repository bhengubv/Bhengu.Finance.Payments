using System.Threading.Tasks;

using Bhengu.Finance.Payments.PayShap.Client;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;

using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.PayShap.Services.Implementations
{
    public class PayShapService : IPayShapService
    {
        private readonly IPayShapClient _client;
        private readonly ILogger<PayShapService> _logger;

        public PayShapService(IPayShapClient client, ILogger<PayShapService> logger)
        {
            _client = client;
            _logger = logger;
        }

        public async Task<PaymentInitiationResponse> InitiatePaymentAsync(PaymentInitiationRequest request)
        {
            _logger.LogInformation("Initiating payment request.");
            return await _client.SendInitiationAsync(request);
        }

        public async Task<PaymentStatusResponse> GetPaymentStatusAsync(PaymentStatusRequest request)
        {
            _logger.LogInformation("Getting payment status.");
            return await _client.SendStatusRequestAsync(request);
        }

        public async Task<PaymentConfirmationResponse> ConfirmPaymentAsync(PaymentConfirmationRequest request)
        {
            _logger.LogInformation("Confirming payment.");
            return await _client.SendConfirmationAsync(request);
        }

        public async Task<CheckDigitVerificationResponse> VerifyCheckDigitAsync(CheckDigitVerificationRequest request)
        {
            _logger.LogInformation("Verifying check digit.");
            return await _client.SendCDVRequestAsync(request);
        }

        public async Task<AccountVerificationResponse> VerifyAccountAsync(AccountVerificationRequest request)
        {
            _logger.LogInformation("Verifying account with bank code: {BankCode}", request.BankCode);
            return await _client.SendAccountVerificationAsync(request);
        }

        public async Task<ProxyResolutionResponse> ResolveProxyAsync(ProxyResolutionRequest request)
        {
            _logger.LogInformation("Resolving proxy.");
            return await _client.ResolveProxyAsync(request);
        }

        public async Task<RegisterProxyResponse> RegisterProxyAsync(RegisterProxyRequest request)
        {
            _logger.LogInformation("Registering proxy.");
            return await _client.RegisterProxyAsync(request);
        }

        public async Task<DeregisterProxyResponse> DeregisterProxyAsync(DeregisterProxyRequest request)
        {
            _logger.LogInformation("Deregistering proxy.");
            return await _client.DeregisterProxyAsync(request);
        }

        public async Task<QueryProxyResponse> QueryProxyAsync(QueryProxyRequest request)
        {
            _logger.LogInformation("Querying proxy.");
            return await _client.QueryProxyAsync(request);
        }

        public async Task<SubscribeEventResponse> SubscribeToEventAsync(SubscribeEventRequest request)
        {
            _logger.LogInformation("Subscribing to events.");
            return await _client.SubscribeToEventAsync(request);
        }

        public async Task<RtcPaymentResponse> InitiateRtcPaymentAsync(RtcPaymentRequest request)
        {
            _logger.LogInformation("Initiating RTC payment.");
            return await _client.InitiateRtcPaymentAsync(request);
        }

        public async Task<SendRtcResponse> SendRtcAsync(SendRtcRequest request)
        {
            _logger.LogInformation("Sending RTC payment.");
            return await _client.SendRtcAsync(request);
        }

        public async Task<EFTPaymentResponse> SendEftAsync(EFTPaymentRequest request)
        {
            _logger.LogInformation("Sending EFT payment.");
            return await _client.SendEftAsync(request);
        }
    }
}
