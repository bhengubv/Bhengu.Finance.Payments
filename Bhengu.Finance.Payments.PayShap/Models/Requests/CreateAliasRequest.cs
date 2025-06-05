namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class CreateAliasRequest
    {
        public string AliasType { get; set; } = string.Empty;
        public string AliasValue { get; set; } = string.Empty;
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
    }
}
