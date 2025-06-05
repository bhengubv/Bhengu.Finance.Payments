using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Metadata
{
    public class TransactionMetadata
    {
        public string TransactionId { get; set; } = string.Empty;
        public string MerchantId { get; set; } = string.Empty;
        public DateTime InitiatedAt { get; set; }
        public string Channel { get; set; } = string.Empty;
        public string Reference { get; set; } = string.Empty;
    }
}
