namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class SendRtcResponse
    {
        public string Status { get; set; } = string.Empty;
        public string TransactionId { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
    }
}
