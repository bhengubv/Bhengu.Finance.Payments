using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Bhengu.Finance.Payments.PayShap.Client;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Exceptions;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Utilities;

using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayShap.Services.Implementations
{
    public class PayShapClient : IPayShapClient
    {
        private readonly HttpClient _httpClient;
        private readonly PayShapSettings _settings;

        public PayShapClient(HttpClient httpClient, IOptions<PayShapSettings> options)
        {
            _httpClient = httpClient;
            _settings = options.Value;
        }

        private async Task<TResponse> PostAsync<TRequest, TResponse>(string endpoint, TRequest request)
        {
            var url = $"{_settings.ApiBaseUrl}{endpoint}";
            var json = JsonSerializer.Serialize(request);
            var signature = PayShapSignatureHelper.GenerateSignature(json, _settings.SignatureKey);

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            httpRequest.Headers.Add("X-API-KEY", _settings.ApiKey);
            httpRequest.Headers.Add("X-SIGNATURE", signature);

            var response = await _httpClient.SendAsync(httpRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new PayShapApiException("PayShap API error", response.StatusCode, content);
            }

            return JsonSerializer.Deserialize<TResponse>(content) ?? throw new JsonException($"Failed to deserialize {typeof(TResponse).Name}");
        }

        public Task<PaymentInitiationResponse> SendInitiationAsync(PaymentInitiationRequest request) =>
            PostAsync<PaymentInitiationRequest, PaymentInitiationResponse>("/v1/payments", request);

        public Task<PaymentStatusResponse> SendStatusRequestAsync(PaymentStatusRequest request) =>
            PostAsync<PaymentStatusRequest, PaymentStatusResponse>("/v1/payments/status", request);

        public Task<PaymentConfirmationResponse> SendConfirmationAsync(PaymentConfirmationRequest request) =>
            PostAsync<PaymentConfirmationRequest, PaymentConfirmationResponse>("/v1/payments/confirm", request);

        public Task<CheckDigitVerificationResponse> SendCDVRequestAsync(CheckDigitVerificationRequest request) =>
            PostAsync<CheckDigitVerificationRequest, CheckDigitVerificationResponse>("/v1/check-digit/verify", request);

        public Task<AccountVerificationResponse> SendAccountVerificationAsync(AccountVerificationRequest request) =>
            PostAsync<AccountVerificationRequest, AccountVerificationResponse>("/v1/accounts/verify", request);

        public Task<ProxyResolutionResponse> ResolveProxyAsync(ProxyResolutionRequest request) =>
            PostAsync<ProxyResolutionRequest, ProxyResolutionResponse>("/v1/proxies/resolve", request);

        public Task<RegisterProxyResponse> RegisterProxyAsync(RegisterProxyRequest request) =>
            PostAsync<RegisterProxyRequest, RegisterProxyResponse>("/v1/proxies/register", request);

        public Task<DeregisterProxyResponse> DeregisterProxyAsync(DeregisterProxyRequest request) =>
            PostAsync<DeregisterProxyRequest, DeregisterProxyResponse>("/v1/proxies/deregister", request);

        public Task<QueryProxyResponse> QueryProxyAsync(QueryProxyRequest request) =>
            PostAsync<QueryProxyRequest, QueryProxyResponse>("/v1/proxies/query", request);

        public Task<SubscribeEventResponse> SubscribeToEventAsync(SubscribeEventRequest request) =>
            PostAsync<SubscribeEventRequest, SubscribeEventResponse>("/v1/events/subscribe", request);

        public Task<RtcPaymentResponse> InitiateRtcPaymentAsync(RtcPaymentRequest request) =>
            PostAsync<RtcPaymentRequest, RtcPaymentResponse>("/v1/rtc/initiate", request);

        public Task<SendRtcResponse> SendRtcAsync(SendRtcRequest request) =>
            PostAsync<SendRtcRequest, SendRtcResponse>("/v1/rtc/send", request);

        public Task<EFTPaymentResponse> SendEftAsync(EFTPaymentRequest request) =>
            PostAsync<EFTPaymentRequest, EFTPaymentResponse>("/v1/eft/transfer", request);
    }
}
