namespace Bhengu.Finance.Payments.Core.Models
{
    public class RefundRequest
    {
        public string TransactionId { get; set; }
        public decimal Amount { get; set; }
        public string? RefundReason { get; set; }
    }
}