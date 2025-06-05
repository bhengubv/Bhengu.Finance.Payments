namespace Bhengu.Finance.Payments.PayShap.Models.Metadata
{
    public class ErrorDetail
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? Field { get; set; }  // Optional: used in validation scenarios
    }
}
