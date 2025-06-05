namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class RtcPaymentResponse
    {
        public string TransactionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string ConfirmationMessage { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
    }
}
