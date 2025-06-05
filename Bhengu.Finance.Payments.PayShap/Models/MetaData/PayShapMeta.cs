using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Metadata
{
    public class PayShapMetadata
    {
        public string EventType { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public DateTime EventTimestamp { get; set; }

        // Optional fields depending on context
        public string? SourceSystem { get; set; }
        public string? CorrelationId { get; set; }
        public string? RequestId { get; set; }
    }
}
