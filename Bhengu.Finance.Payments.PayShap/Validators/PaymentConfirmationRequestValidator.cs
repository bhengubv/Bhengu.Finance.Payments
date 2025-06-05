using Bhengu.Finance.Payments.PayShap.Models.Requests;

namespace Bhengu.Finance.Payments.PayShap.Validators
{
    public static class PaymentConfirmationRequestValidator
    {
        public static bool IsValid(PaymentConfirmationRequest request, out string error)
        {
            if (string.IsNullOrWhiteSpace(request.PaymentId))
            {
                error = "PaymentId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.ConfirmationCode))
            {
                error = "ConfirmationCode is required.";
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
