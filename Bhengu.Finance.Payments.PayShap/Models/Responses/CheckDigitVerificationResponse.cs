namespace Bhengu.Finance.Payments.PayShap.Models.Responses
{
    public class CheckDigitVerificationResponse
    {
        public bool IsValid { get; set; }
        public string? Message { get; set; }
    }
}
