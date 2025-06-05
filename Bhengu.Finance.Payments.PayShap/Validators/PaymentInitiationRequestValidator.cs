using System;

using Bhengu.Finance.Payments.PayShap.Models.Requests;

namespace Bhengu.Finance.Payments.PayShap.Validators
{
    public static class PaymentInitiationRequestValidator
    {
        public static void Validate(PaymentInitiationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (!decimal.TryParse(request.Amount, out var amount) || amount <= 0)
                throw new ArgumentException("Amount must be a valid positive number.");


            if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
                throw new ArgumentException("Currency must be a valid 3-letter ISO code.");

            if (request.Recipient == null)
                throw new ArgumentException("Recipient is required.");

            if (string.IsNullOrWhiteSpace(request.Recipient.IdentifierType))
                throw new ArgumentException("Recipient identifier type is required.");

            if (string.IsNullOrWhiteSpace(request.Recipient.IdentifierValue))
                throw new ArgumentException("Recipient identifier value is required.");

            if (string.IsNullOrWhiteSpace(request.Reference))
                throw new ArgumentException("Reference is required.");
        }
    }
}
