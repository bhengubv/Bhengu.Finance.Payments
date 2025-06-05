using System.Text.Json.Serialization;

namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class PayShapEvent<T>
    {
        [JsonPropertyName("eventType")]
        public string EventType { get; set; } = string.Empty;

        [JsonPropertyName("eventTime")]
        public string EventTime { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public T Data { get; set; } = default!;
    }
}
