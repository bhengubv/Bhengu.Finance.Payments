using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Events
{
    public class PaymentInitiatedEvent
    {
        public string TransactionId { get; set; } = string.Empty;
        public string ExternalRef { get; set; } = string.Empty;
        public DateTime InitiatedAt { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
