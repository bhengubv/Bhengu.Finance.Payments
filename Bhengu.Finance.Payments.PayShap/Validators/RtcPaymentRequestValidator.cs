using System.Collections.Generic;

using Bhengu.Finance.Payments.PayShap.Models.Requests;

namespace Bhengu.Finance.Payments.PayShap.Validators
{
    public static class RtcPaymentRequestValidator
    {
        public static List<string> Validate(RtcPaymentRequest request)
        {
            var errors = new List<string>();

            if (request == null)
            {
                errors.Add("Request cannot be null.");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(request.Amount))
                errors.Add("Amount is required.");

            if (string.IsNullOrWhiteSpace(request.Reference))
                errors.Add("Reference is required.");

            if (request.Initiator == null)
                errors.Add("Initiator is required.");
            else
            {
                if (string.IsNullOrWhiteSpace(request.Initiator.AccountNumber))
                    errors.Add("Initiator account number is required.");

                if (string.IsNullOrWhiteSpace(request.Initiator.BankCode))
                    errors.Add("Initiator bank code is required.");
            }

            if (request.Recipient == null)
                errors.Add("Recipient is required.");
            else
            {
                if (string.IsNullOrWhiteSpace(request.Recipient.AccountNumber))
                    errors.Add("Recipient account number is required.");

                if (string.IsNullOrWhiteSpace(request.Recipient.BankCode))
                    errors.Add("Recipient bank code is required.");
            }

            return errors;
        }
    }
}
    