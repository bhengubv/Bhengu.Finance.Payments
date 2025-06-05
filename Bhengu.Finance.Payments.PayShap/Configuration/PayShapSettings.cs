namespace Bhengu.Finance.Payments.PayShap.Configuration
{
    public class PayShapSettings
    {
        public string ApiBaseUrl { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
        public string ApiSecret { get; set; } = string.Empty;
        public string SignatureKey { get; set; } = string.Empty;
        public string MerchantId { get; set; } = string.Empty;
    }
}
