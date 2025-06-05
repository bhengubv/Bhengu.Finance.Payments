using System;
using System.Text.Json.Serialization;

namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class InboundPaymentAcknowledgementEvent
    {
        [JsonPropertyName("event_id")]
        public string EventId { get; set; } = string.Empty;

        [JsonPropertyName("event_type")]
        public string EventType { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("transaction_id")]
        public string TransactionId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("reference")]
        public string Reference { get; set; } = string.Empty;

        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        [JsonPropertyName("currency")]
        public string Currency { get; set; } = "ZAR";

        [JsonPropertyName("receiver_account")]
        public string ReceiverAccount { get; set; } = string.Empty;

        [JsonPropertyName("sender_account")]
        public string SenderAccount { get; set; } = string.Empty;
    }
}
