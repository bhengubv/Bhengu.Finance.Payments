namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class PaymentInitiationRequest
    {
        public string Amount { get; set; } = string.Empty;
        public string Currency { get; set; } = "ZAR";
        public string Reference { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public RecipientDetails Recipient { get; set; } = new();

        // Optional: Add a Validate() method here later
    }

    public class RecipientDetails
    {
        public string Name { get; set; } = string.Empty;
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
        public string IdentifierType { get; set; } = string.Empty;
        public string IdentifierValue { get; set; } = string.Empty;
    }
}
