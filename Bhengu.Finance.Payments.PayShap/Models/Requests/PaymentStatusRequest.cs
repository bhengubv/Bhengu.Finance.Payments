namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class PaymentStatusRequest
    {
        public string PaymentId { get; set; } = string.Empty;
        public string MerchantId { get; set; } = string.Empty;
    }
}
