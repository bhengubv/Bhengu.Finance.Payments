using System.Threading.Tasks;

using Bhengu.Finance.Payments.PayShap.Models.Events;

namespace Bhengu.Finance.Payments.PayShap.Services.Interfaces
{
    public interface IPayShapEventHandler
    {
        Task HandleEventAsync(WebhookEventRequest eventRequest);
    }
}
