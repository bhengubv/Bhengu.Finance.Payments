namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class CheckDigitVerificationRequest
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
    }
}
