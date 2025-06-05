namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class AccountVerificationRequest
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;

        // Optional - Include only if used in Electrum AVS-R
        public string IdentifierType { get; set; } = string.Empty;
        public string IdentifierValue { get; set; } = string.Empty;
    }
}
