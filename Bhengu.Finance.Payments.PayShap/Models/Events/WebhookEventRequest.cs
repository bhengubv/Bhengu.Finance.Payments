namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class WebhookEventRequest
    {
        public string EventType { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string EventTime { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty; // Raw JSON payload
        public string Signature { get; set; } = string.Empty;
    }
}
