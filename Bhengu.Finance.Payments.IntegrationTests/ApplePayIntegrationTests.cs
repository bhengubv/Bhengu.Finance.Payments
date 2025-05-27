using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Bhengu.Finance.Payments.ApplePay.Services;
using Bhengu.Finance.Payments.Core.Models;
using System.Threading.Tasks;
using System.Net.Http;

public class ApplePayIntegrationTests
{
    private readonly IApplePayService _service;

    public ApplePayIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Development.json")
            .Build();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<ApplePayService>();
        var client = new HttpClient();
        _service = new ApplePayService(client, config, logger);
    }

    [Fact(Skip = "Set real config values to run")]
    public async Task ShouldCallRealEndpoint()
    {
        var result = await _service.InitiateAsync(new PaymentRequest
        {
            Amount = 123.45m,
            ItemName = "Integration Test"
        });

        Assert.NotNull(result);
    }
}