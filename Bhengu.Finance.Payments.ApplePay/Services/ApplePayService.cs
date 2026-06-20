using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.ApplePay.Services
{
    public class ApplePayService : IApplePayService, IPaymentService, ISubscriptionService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        private readonly ILogger<ApplePayService> _logger;

        public ApplePayService(HttpClient httpClient, IConfiguration config, ILogger<ApplePayService> logger)
        {
            _httpClient = httpClient;
            _config = config;
            _logger = logger;
        }

        public async Task<string> InitiateAsync(PaymentRequest request)
        {
            _logger.LogInformation("Initiate payment via ApplePay");
            return await Task.FromResult("Simulated ApplePay payment response");
        }

        public async Task<string> RefundAsync(RefundRequest request)
        {
            _logger.LogInformation("Refund request via ApplePay");
            return await Task.FromResult("Simulated ApplePay refund response");
        }

        public async Task<string> CreateSubscriptionAsync(SubscriptionRequest request)
        {
            _logger.LogInformation("Create subscription via ApplePay");
            return await Task.FromResult("Simulated ApplePay subscription creation");
        }

        public async Task<string> CancelSubscriptionAsync(string subscriptionId)
        {
            _logger.LogInformation("Cancel subscription via ApplePay");
            return await Task.FromResult("Simulated ApplePay subscription cancellation");
        }

        public async Task<string> GetSubscriptionStatusAsync(string subscriptionId)
        {
            _logger.LogInformation("Get subscription status via ApplePay");
            return await Task.FromResult("Simulated ApplePay subscription status");
        }
    }

    public static class ApplePayServiceExtensions
    {
        public static IServiceCollection AddApplePayPayments(this IServiceCollection services)
        {
            services.AddHttpClient<IPaymentService, ApplePayService>();
            services.AddHttpClient<ISubscriptionService, ApplePayService>();
            return services;
        }
    }
}