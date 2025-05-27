using Xunit;
using Moq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Bhengu.Finance.Payments.ApplePay.Services;
using Bhengu.Finance.Payments.Core.Models;
using System.Threading.Tasks;

public class ApplePayServiceTests
{
    private readonly IApplePayService _service;

    public ApplePayServiceTests()
    {
        var logger = new Mock<ILogger<ApplePayService>>();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var httpClient = new HttpClient();
        _service = new ApplePayService(httpClient, config, logger.Object);
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