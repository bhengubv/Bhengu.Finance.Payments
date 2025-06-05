namespace Bhengu.Finance.Payments.PayShap.Models.Requests
{
    public class DeregisterProxyRequest
    {
        public string IdentifierType { get; set; } = string.Empty;
        public string IdentifierValue { get; set; } = string.Empty;
    }
}
