using System.Collections.Generic;

using Bhengu.Finance.Payments.PayShap.Models.Requests;

namespace Bhengu.Finance.Payments.PayShap.Validators
{
    public static class CreateAliasRequestValidator
    {
        public static List<string> Validate(CreateAliasRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.AliasType))
                errors.Add("AliasType is required.");

            if (string.IsNullOrWhiteSpace(request.AliasValue))
                errors.Add("AliasValue is required.");

            if (string.IsNullOrWhiteSpace(request.AccountNumber))
                errors.Add("AccountNumber is required.");

            if (string.IsNullOrWhiteSpace(request.BankCode))
                errors.Add("BankCode is required.");

            return errors;
        }
    }
}
