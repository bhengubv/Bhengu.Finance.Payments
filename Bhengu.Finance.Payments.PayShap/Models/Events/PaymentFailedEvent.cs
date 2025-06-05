using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class PaymentFailedEvent
    {
        public string TransactionId { get; set; } = string.Empty;
        public string ExternalRef { get; set; } = string.Empty;
        public DateTime FailedAt { get; set; }
        public string FailureReason { get; set; } = string.Empty;
    }
}
