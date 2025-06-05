using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Metadata
{
    public class SuccessResponseMetadata
    {
        public string RequestId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }
}
