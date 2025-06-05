namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class RtcPaymentRequest
    {
        public string Amount { get; set; } = string.Empty;
        public string Currency { get; set; } = "ZAR";
        public string Reference { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        public RtcInitiator Initiator { get; set; } = new();
        public RtcRecipient Recipient { get; set; } = new();
    }

    public class RtcInitiator
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class RtcRecipient
    {
        public string AccountNumber { get; set; } = string.Empty;
        public string BankCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string IdentifierType { get; set; } = string.Empty;
        public string IdentifierValue { get; set; } = string.Empty;
    }
}
