// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Exceptions;

/// <summary>
/// The provider was unreachable or returned a server-side error (network timeout, 5xx).
/// Distinct from a declined payment — the request never reached a settlement decision.
/// </summary>
public sealed class ProviderUnavailableException : BhenguPaymentException
{
    public ProviderUnavailableException(
        string providerName,
        string? providerErrorMessage = null,
        Exception? innerException = null)
        : base(
            providerName,
            $"{providerName} is unavailable" + (providerErrorMessage is null ? "" : $": {providerErrorMessage}"),
            providerErrorCode: "unavailable",
            providerErrorMessage,
            innerException)
    {
    }
}
