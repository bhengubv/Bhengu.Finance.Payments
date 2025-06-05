namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class EFTPaymentRequest
    {
        public string Amount { get; set; } = string.Empty;
        public string Currency { get; set; } = "ZAR"; // Default currency
        public string Reference { get; set; } = string.Empty;
        public string SenderAccount { get; set; } = string.Empty;
        public string ReceiverAccount { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
    }
}
