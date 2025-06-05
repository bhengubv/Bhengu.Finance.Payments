namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class PaymentConfirmationResponse
    {
        public string ConfirmationId { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ConfirmedAt { get; set; } = string.Empty;
        public string? Reference { get; set; }
        public decimal? Amount { get; set; }
        public string? Currency { get; set; }
    }
}
