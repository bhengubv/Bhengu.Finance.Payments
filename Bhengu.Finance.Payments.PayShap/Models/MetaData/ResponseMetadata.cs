namespace Bhengu.Finance.Payments.PayShap.Models.Metadata
{
    public class ResponseMetadata
    {
        public string CorrelationId { get; set; } = string.Empty;
        public string ResponseTimestamp { get; set; } = string.Empty;
        public string RequestId { get; set; } = string.Empty;
    }
}
