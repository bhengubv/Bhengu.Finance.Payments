using Bhengu.Finance.Payments.PayShap.Models.Requests;

namespace Bhengu.Finance.Payments.PayShap.Utilities
{
    public static class PayShapRequestBuilder
    {
        public static string BuildInitiationRequestPayload(PaymentInitiationRequest request)
        {
            var payload = new
            {
                amount = request.Amount,
                currency = request.Currency,
                reference = request.Reference,
                description = request.Description,
                recipient = new
                {
                    name = request.Recipient.Name,
                    accountNumber = request.Recipient.AccountNumber,
                    bankCode = request.Recipient.BankCode,
                    identifierType = request.Recipient.IdentifierType,
                    identifierValue = request.Recipient.IdentifierValue
                }
            };

            return System.Text.Json.JsonSerializer.Serialize(payload);
        }
    }
}
