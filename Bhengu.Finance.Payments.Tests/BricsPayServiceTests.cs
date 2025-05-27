using Xunit;
using Moq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Bhengu.Finance.Payments.BricsPay.Services;
using Bhengu.Finance.Payments.Core.Models;
using System.Threading.Tasks;

public class BricsPayServiceTests
{
    private readonly IBricsPayService _service;

    public BricsPayServiceTests()
    {
        var logger = new Mock<ILogger<BricsPayService>>();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var httpClient = new HttpClient();
        _service = new BricsPayService(httpClient, config, logger.Object);
    }

    [Fact]
    public async Task InitiateAsync_ShouldReturnMockedResponse()
    {
        var result = await _service.InitiateAsync(new PaymentRequest
        {
            Amount = 100.0m,
            ItemName = "Test Item"
        });

        Assert.Contains("Simulated", result);
    }
}