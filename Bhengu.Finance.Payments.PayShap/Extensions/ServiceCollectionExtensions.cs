//using Bhengu.Finance.Payments.PayShap.Clients;
using Bhengu.Finance.Payments.PayShap.Client;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Services.Implementations;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bhengu.Finance.Payments.PayShap.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPayShapServices(this IServiceCollection services, IConfiguration configuration)
        {
            // Bind configuration
            services.Configure<PayShapSettings>(configuration.GetSection(nameof(PayShapSettings)));

            // Register HttpClient and client abstraction
            services.AddHttpClient<IPayShapClient, PayShapClient>();

            // Register core PayShap service
            services.AddScoped<IPayShapService, PayShapService>();

            return services;
        }
    }
}
