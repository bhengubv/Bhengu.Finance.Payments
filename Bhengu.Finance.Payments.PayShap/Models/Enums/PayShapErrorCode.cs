using System.Runtime.Serialization;

namespace Bhengu.Finance.Payments.PayShap.Models.Enums
{
    public enum PayShapErrorCode
    {
        [EnumMember(Value = "INVALID_SIGNATURE")]
        InvalidSignature,

        [EnumMember(Value = "UNAUTHORIZED")]
        Unauthorized,

        [EnumMember(Value = "MALFORMED_REQUEST")]
        MalformedRequest,

        [EnumMember(Value = "DUPLICATE_TRANSACTION")]
        DuplicateTransaction,

        [EnumMember(Value = "INSUFFICIENT_FUNDS")]
        InsufficientFunds,

        [EnumMember(Value = "RECIPIENT_NOT_FOUND")]
        RecipientNotFound,

        [EnumMember(Value = "UNKNOWN_ERROR")]
        UnknownError
    }
}
