using System.Threading.Tasks;

using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;

namespace Bhengu.Finance.Payments.PayShap.Client
{
    public interface IPayShapClient
    {
        Task<PaymentInitiationResponse> SendInitiationAsync(PaymentInitiationRequest request);
        Task<PaymentStatusResponse> SendStatusRequestAsync(PaymentStatusRequest request);
        Task<PaymentConfirmationResponse> SendConfirmationAsync(PaymentConfirmationRequest request);

        Task<CheckDigitVerificationResponse> SendCDVRequestAsync(CheckDigitVerificationRequest request);
        Task<AccountVerificationResponse> SendAccountVerificationAsync(AccountVerificationRequest request);

        Task<ProxyResolutionResponse> ResolveProxyAsync(ProxyResolutionRequest request);
        Task<RegisterProxyResponse> RegisterProxyAsync(RegisterProxyRequest request);
        Task<DeregisterProxyResponse> DeregisterProxyAsync(DeregisterProxyRequest request);
        Task<QueryProxyResponse> QueryProxyAsync(QueryProxyRequest request);

        Task<SubscribeEventResponse> SubscribeToEventAsync(SubscribeEventRequest request);
        Task<RtcPaymentResponse> InitiateRtcPaymentAsync(RtcPaymentRequest request);
        Task<SendRtcResponse> SendRtcAsync(SendRtcRequest request);
        Task<EFTPaymentResponse> SendEftAsync(EFTPaymentRequest request);
    }
}
