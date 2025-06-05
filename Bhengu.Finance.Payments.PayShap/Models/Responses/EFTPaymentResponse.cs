using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class EFTPaymentResponse
    {
        public string TransactionId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public DateTime ProcessedAt { get; set; }
    }
}
