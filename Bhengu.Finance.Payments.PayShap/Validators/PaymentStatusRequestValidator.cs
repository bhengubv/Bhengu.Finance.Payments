using Bhengu.Finance.Payments.PayShap.Models.Requests;

namespace Bhengu.Finance.Payments.PayShap.Validators
{
    public static class PaymentStatusRequestValidator
    {
        public static bool IsValid(PaymentStatusRequest request, out string error)
        {
            if (string.IsNullOrWhiteSpace(request.PaymentId))
            {
                error = "PaymentId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.MerchantId))
            {
                error = "MerchantId is required.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }
}
