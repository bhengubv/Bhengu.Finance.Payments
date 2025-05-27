using Xunit;
using Moq;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Bhengu.Finance.Payments.PayFast.Services;
using Bhengu.Finance.Payments.Core.Models;
using System.Threading.Tasks;

public class PayFastServiceTests
{
    private readonly IPayFastService _service;

    public PayFastServiceTests()
    {
        var logger = new Mock<ILogger<PayFastService>>();
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var httpClient = new HttpClient();
        _service = new PayFastService(httpClient, config, logger.Object);
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