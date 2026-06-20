using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http;

namespace Bhengu.Finance.Payments.BricsPay.Services
{
    public class BricsPayService : IBricsPayService, IPaymentService, ISubscriptionService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        private readonly ILogger<BricsPayService> _logger;

        public BricsPayService(HttpClient httpClient, IConfiguration config, ILogger<BricsPayService> logger)
        {
            _httpClient = httpClient;
            _config = config;
            _logger = logger;
        }

        public async Task<string> InitiateAsync(PaymentRequest request)
        {
            _logger.LogInformation("Initiate payment via BricsPay");
            return await Task.FromResult("Simulated BricsPay payment response");
        }

        public async Task<string> RefundAsync(RefundRequest request)
        {
            _logger.LogInformation("Refund request via BricsPay");
            return await Task.FromResult("Simulated BricsPay refund response");
        }

        public async Task<string> CreateSubscriptionAsync(SubscriptionRequest request)
        {
            _logger.LogInformation("Create subscription via BricsPay");
            return await Task.FromResult("Simulated BricsPay subscription creation");
        }

        public async Task<string> CancelSubscriptionAsync(string subscriptionId)
        {
            _logger.LogInformation("Cancel subscription via BricsPay");
            return await Task.FromResult("Simulated BricsPay subscription cancellation");
        }

        public async Task<string> GetSubscriptionStatusAsync(string subscriptionId)
        {
            _logger.LogInformation("Get subscription status via BricsPay");
            return await Task.FromResult("Simulated BricsPay subscription status");
        }
    }

    public static class BricsPayServiceExtensions
    {
        public static IServiceCollection AddBricsPayPayments(this IServiceCollection services)
        {
            services.AddHttpClient<IPaymentService, BricsPayService>();
            services.AddHttpClient<ISubscriptionService, BricsPayService>();
            return services;
        }
    }
}