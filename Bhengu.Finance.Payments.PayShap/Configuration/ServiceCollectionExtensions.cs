using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayShap.Configuration
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddPayShapConfiguration(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<PayShapSettings>(configuration.GetSection("PayShapSettings"));
            return services;
        }
    }
}
