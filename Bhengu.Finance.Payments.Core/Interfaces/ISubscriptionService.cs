using System.Threading.Tasks;
using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.Core.Interfaces
{
    public interface ISubscriptionService
    {
        Task<string> CreateSubscriptionAsync(SubscriptionRequest request);
        Task<string> CancelSubscriptionAsync(string subscriptionId);
        Task<string> GetSubscriptionStatusAsync(string subscriptionId);
    }
}