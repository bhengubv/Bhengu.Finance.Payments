namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class PaymentConfirmationRequest
    {
        public string PaymentId { get; set; } = string.Empty;
        public string ConfirmationCode { get; set; } = string.Empty;
        public string MerchantId { get; set; } = string.Empty;
    }
}
