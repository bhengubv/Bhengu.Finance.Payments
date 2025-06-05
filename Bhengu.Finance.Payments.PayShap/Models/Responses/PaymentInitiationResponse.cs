namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class PaymentInitiationResponse
    {
        public string TransactionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
        public string UpdatedAt { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
    }
}
