namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class SendRtcRequest
    {
        public string SenderAccount { get; set; } = string.Empty;
        public string ReceiverAccount { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }
}
