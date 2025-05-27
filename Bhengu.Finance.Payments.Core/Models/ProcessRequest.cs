namespace Bhengu.Finance.Payments.Core.Models
{
    public class ProcessRequest
    {
        public string TransactionId { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "ZAR";
        public string? Description { get; set; }
    }
}