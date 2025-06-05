namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class ProxyResolutionResponse
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
        public string AccountName { get; set; } = string.Empty;
        public string ResponseCode { get; set; } = string.Empty;
        public string ResponseMessage { get; set; } = string.Empty;
    }
}
