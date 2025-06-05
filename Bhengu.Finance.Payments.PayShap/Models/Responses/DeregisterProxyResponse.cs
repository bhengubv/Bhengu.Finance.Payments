namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class DeregisterProxyResponse
    {
        public bool Success { get; set; }
        public string ResponseCode { get; set; } = string.Empty;
        public string ResponseMessage { get; set; } = string.Empty;
    }
}
