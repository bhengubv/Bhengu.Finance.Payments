namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class QueryProxyResponse
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
        public string RegistrationStatus { get; set; } = string.Empty;
        public string ResponseCode { get; set; } = string.Empty;
        public string ResponseMessage { get; set; } = string.Empty;
    }
}
