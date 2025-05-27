using System.Threading.Tasks;
using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.Core.Interfaces
{
    public interface IPaymentService
    {
        Task<string> InitiateAsync(PaymentRequest request);
        Task<string> RefundAsync(RefundRequest request);
    }
}