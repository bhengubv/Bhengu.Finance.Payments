using System;
using System.Text.Json.Serialization;

namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class InboundPaymentEvent
    {
        [JsonPropertyName("event_id")]
        public string EventId { get; set; } = string.Empty;

        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("data")]
        public InboundEventPayload Data { get; set; } = new InboundEventPayload();
    }
}
