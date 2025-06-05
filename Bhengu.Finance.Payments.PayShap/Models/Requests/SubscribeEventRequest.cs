namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class SubscribeEventRequest
    {
        public string Url { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty; // e.g., "payment.initiated"
    }
}