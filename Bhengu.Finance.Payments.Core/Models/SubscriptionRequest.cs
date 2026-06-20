namespace Bhengu.Finance.Payments.Core.Models
{
    public class SubscriptionRequest
    {
        public string PlanId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string Currency { get; set; } = "ZAR";
        public string Frequency { get; set; } = "monthly";
    }
}