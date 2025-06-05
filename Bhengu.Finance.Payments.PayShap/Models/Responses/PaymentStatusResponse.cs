namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class PaymentStatusResponse
    {
        public string TransactionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public string? Reference { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
    }
}
