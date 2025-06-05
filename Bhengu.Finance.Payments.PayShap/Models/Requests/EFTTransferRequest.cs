using System;

namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class EFTTransferRequest
    {
        public string SourceAccountNumber { get; set; } = string.Empty;
        public string SourceBankCode { get; set; } = string.Empty;
        public string DestinationAccountNumber { get; set; } = string.Empty;
        public string DestinationBankCode { get; set; } = string.Empty;
        public string Amount { get; set; } = string.Empty;
        public string Currency { get; set; } = "ZAR";
        public string Reference { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime TransactionDate { get; set; } = DateTime.UtcNow;
    }
}
