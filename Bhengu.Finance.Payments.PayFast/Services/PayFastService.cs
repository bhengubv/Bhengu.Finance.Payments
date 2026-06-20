using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayFast.Services
{
    public class PayFastService : IPayFastService,IPaymentService, ISubscriptionService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        private readonly ILogger<PayFastService> _logger;

        public PayFastService(HttpClient httpClient, IConfiguration config, ILogger<PayFastService> logger)
        {
            _httpClient = httpClient;
            _config = config;
            _logger = logger;
        }

        public async Task<string> InitiateAsync(PaymentRequest request)
        {
            _logger.LogInformation("Initiate payment via PayFast");
            return await Task.FromResult("Simulated PayFast payment response");
        }

        public async Task<string> RefundAsync(RefundRequest request)
        {
            _logger.LogInformation("Refund request via PayFast");
            return await Task.FromResult("Simulated PayFast refund response");
        }

        public async Task<string> CreateSubscriptionAsync(SubscriptionRequest request)
        {
            _logger.LogInformation("Create subscription via PayFast");
            return await Task.FromResult("Simulated PayFast subscription creation");
        }

        public async Task<string> CancelSubscriptionAsync(string subscriptionId)
        {
            _logger.LogInformation("Cancel subscription via PayFast");
            return await Task.FromResult("Simulated PayFast subscription cancellation");
        }

        public async Task<string> GetSubscriptionStatusAsync(string subscriptionId)
        {
            _logger.LogInformation("Get subscription status via PayFast");
            return await Task.FromResult("Simulated PayFast subscription status");
        }
    }

    public static class PayFastServiceExtensions
    {
        public static IServiceCollection AddPayFastPayments(this IServiceCollection services)
        {
            services.AddHttpClient<IPaymentService, PayFastService>();
            services.AddHttpClient<ISubscriptionService, PayFastService>();
            return services;
        }
    }
}