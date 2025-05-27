namespace Bhengu.Finance.Payments.Core.Models
{
    public class PaymentRequest
    {
        public decimal Amount { get; set; }
        public string ItemName { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
    }
}