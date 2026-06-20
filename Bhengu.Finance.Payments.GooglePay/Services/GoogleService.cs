using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.Google.Services
{
    public class GoogleService : IGoogleService, IPaymentService, ISubscriptionService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _config;
        private readonly ILogger<GoogleService> _logger;

        public GoogleService(HttpClient httpClient, IConfiguration config, ILogger<GoogleService> logger)
        {
            _httpClient = httpClient;
            _config = config;
            _logger = logger;
        }

        public async Task<string> InitiateAsync(PaymentRequest request)
        {
            _logger.LogInformation("Initiate payment via Google");
            return await Task.FromResult("Simulated Google payment response");
        }

        public async Task<string> RefundAsync(RefundRequest request)
        {
            _logger.LogInformation("Refund request via Google");
            return await Task.FromResult("Simulated Google refund response");
        }

        public async Task<string> CreateSubscriptionAsync(SubscriptionRequest request)
        {
            _logger.LogInformation("Create subscription via Google");
            return await Task.FromResult("Simulated Google subscription creation");
        }

        public async Task<string> CancelSubscriptionAsync(string subscriptionId)
        {
            _logger.LogInformation("Cancel subscription via Google");
            return await Task.FromResult("Simulated Google subscription cancellation");
        }

        public async Task<string> GetSubscriptionStatusAsync(string subscriptionId)
        {
            _logger.LogInformation("Get subscription status via Google");
            return await Task.FromResult("Simulated Google subscription status");
        }
    }

    public static class GoogleServiceExtensions
    {
        public static IServiceCollection AddGooglePayments(this IServiceCollection services)
        {
            services.AddHttpClient<IPaymentService, GoogleService>();
            services.AddHttpClient<ISubscriptionService, GoogleService>();
            return services;
        }
    }
}