using System.Text.Json.Serialization;

namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class InboundEventPayload
    {
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

        [JsonPropertyName("sender_account")]
        public string SenderAccount { get; set; } = string.Empty;

        [JsonPropertyName("receiver_account")]
        public string ReceiverAccount { get; set; } = string.Empty;
    }
}
