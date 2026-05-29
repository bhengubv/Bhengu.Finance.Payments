// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Exceptions;

/// <summary>
/// The provider explicitly declined the payment (insufficient funds, fraud, bad card, etc).
/// </summary>
public sealed class PaymentDeclinedException : BhenguPaymentException
{
    public PaymentDeclinedException(
        string providerName,
        string? providerErrorCode = null,
        string? providerErrorMessage = null,
        Exception? innerException = null)
        : base(
            providerName,
            $"Payment declined by {providerName}" + (providerErrorMessage is null ? "" : $": {providerErrorMessage}"),
            providerErrorCode,
            providerErrorMessage,
            innerException)
    {
    }
}
