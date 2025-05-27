using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Bhengu.Finance.Payments.BricsPay.Services;
using Bhengu.Finance.Payments.Core.Models;
using System.Threading.Tasks;
using System.Net.Http;

public class BricsPayIntegrationTests
{
    private readonly IBricsPayService _service;

    public BricsPayIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Development.json")
            .Build();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<BricsPayService>();
        var client = new HttpClient();
        _service = new BricsPayService(client, config, logger);
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