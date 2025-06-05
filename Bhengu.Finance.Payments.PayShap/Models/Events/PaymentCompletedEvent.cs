using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class PaymentCompletedEvent
    {
        public string TransactionId { get; set; } = string.Empty;
        public string ExternalRef { get; set; } = string.Empty;
        public DateTime CompletedAt { get; set; }
        public string PayerName { get; set; } = string.Empty;
        public string RecipientName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
