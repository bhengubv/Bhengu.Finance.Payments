using System;
using System.Collections.Generic;
using System.Linq;

using Bhengu.Finance.Payments.PayShap.Models.Requests;

namespace Bhengu.Finance.Payments.PayShap.Validators
{
    public static class InitiatePaymentRequestValidator
    {
        public static List<string> Validate(PaymentInitiationRequest request)
        {
            var errors = new List<string>();

            if (request == null)
            {
                errors.Add("Request cannot be null.");
                return errors;
            }

            if (string.IsNullOrWhiteSpace(request.Amount))
                errors.Add("Amount is required.");

            if (!decimal.TryParse(request.Amount, out var amount) || amount <= 0)
                errors.Add("Amount must be a valid number greater than 0.");

            if (string.IsNullOrWhiteSpace(request.Currency))
                errors.Add("Currency is required.");

            if (string.IsNullOrWhiteSpace(request.Reference))
                errors.Add("Reference is required.");

            if (request.Recipient == null)
            {
                errors.Add("Recipient is required.");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Recipient.Name))
                    errors.Add("Recipient name is required.");

                if (string.IsNullOrWhiteSpace(request.Recipient.AccountNumber))
                    errors.Add("Recipient account number is required.");

                if (string.IsNullOrWhiteSpace(request.Recipient.BankCode))
                    errors.Add("Recipient bank code is required.");

                if (string.IsNullOrWhiteSpace(request.Recipient.IdentifierType))
                    errors.Add("Recipient identifier type is required.");

                if (string.IsNullOrWhiteSpace(request.Recipient.IdentifierValue))
                    errors.Add("Recipient identifier value is required.");
            }

            return errors;
        }
    }
}
