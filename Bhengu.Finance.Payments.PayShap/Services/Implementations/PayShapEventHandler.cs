using System.Text.Json;
using System.Threading.Tasks;

using Bhengu.Finance.Payments.PayShap.Models.Events;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;

using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.PayShap.Services.Implementations
{
    public class PayShapEventHandler : IPayShapEventHandler
    {
        private readonly ILogger<PayShapEventHandler> _logger;

        public PayShapEventHandler(ILogger<PayShapEventHandler> logger)
        {
            _logger = logger;
        }

        public Task HandleEventAsync(WebhookEventRequest eventRequest)
        {
            _logger.LogInformation("Received webhook event: {EventType} with ID {EventId}", eventRequest.EventType, eventRequest.EventId);

            // You could deserialize the Data here depending on the event type
            switch (eventRequest.EventType)
            {
                case "payment_initiated":
                    _logger.LogInformation("Payment initiated payload: {Data}", eventRequest.Data);
                    break;

                case "payment_confirmed":
                    _logger.LogInformation("Payment confirmed payload: {Data}", eventRequest.Data);
                    break;

                default:
                    _logger.LogWarning("Unhandled event type: {EventType}", eventRequest.EventType);
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
