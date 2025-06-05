namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class AccountVerificationResponse
    {
        public bool IsVerified { get; set; }  // was: IsValid
        public string AccountName { get; set; } = string.Empty;
        public string ResponseCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty; // was: ResponseMessage
    }
}
