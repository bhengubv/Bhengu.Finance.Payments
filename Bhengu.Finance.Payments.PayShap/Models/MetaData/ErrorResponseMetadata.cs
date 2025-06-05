using System;
using System.Collections.Generic;

namespace Bhengu.Finance.Payments.PayShap.Models.Metadata
{
    public class ErrorResponseMetadata
    {
        public string RequestId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }

        public List<ErrorDetail> Errors { get; set; } = new();
    }
}
