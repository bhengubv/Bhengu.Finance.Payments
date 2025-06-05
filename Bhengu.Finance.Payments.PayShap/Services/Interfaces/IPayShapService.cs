using System.Threading.Tasks;

using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;

namespace Bhengu.Finance.Payments.PayShap.Services.Interfaces
{
    public interface IPayShapService
    {
        Task<PaymentInitiationResponse> InitiatePaymentAsync(PaymentInitiationRequest request);
        Task<PaymentStatusResponse> GetPaymentStatusAsync(PaymentStatusRequest request);
        Task<PaymentConfirmationResponse> ConfirmPaymentAsync(PaymentConfirmationRequest request);
        Task<CheckDigitVerificationResponse> VerifyCheckDigitAsync(CheckDigitVerificationRequest request);
        Task<AccountVerificationResponse> VerifyAccountAsync(AccountVerificationRequest request);
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
