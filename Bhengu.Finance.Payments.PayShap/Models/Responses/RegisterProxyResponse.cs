namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class RegisterProxyResponse
    {
        public bool Success { get; set; }
        public string ReferenceId { get; set; } = string.Empty;
        public string ResponseCode { get; set; } = string.Empty;
        public string ResponseMessage { get; set; } = string.Empty;
    }
}
