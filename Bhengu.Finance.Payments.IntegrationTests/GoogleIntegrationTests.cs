using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Bhengu.Finance.Payments.Google.Services;
using Bhengu.Finance.Payments.Core.Models;
using System.Net.Http;
using System.Threading.Tasks;

public class GoogleIntegrationTests
{
    private readonly IGoogleService _service;

    public GoogleIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.Development.json")
            .Build();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logger = loggerFactory.CreateLogger<GoogleService>();
        var client = new HttpClient();
        _service = new GoogleService(client, config, logger);
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