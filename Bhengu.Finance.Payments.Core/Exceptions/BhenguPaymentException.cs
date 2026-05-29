// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Exceptions;

/// <summary>
/// Base exception for all Bhengu.Finance.Payments failures.
/// Consumers can <c>catch (BhenguPaymentException)</c> to handle any payment-related failure uniformly,
/// or catch a specific derived type for finer-grained handling.
/// </summary>
public class BhenguPaymentException : Exception
{
    /// <summary>The provider that raised the failure (e.g. "payfast", "stripe", "bricspay").</summary>
    public string ProviderName { get; }

    /// <summary>The provider's raw error code, if any.</summary>
    public string? ProviderErrorCode { get; }

    /// <summary>The provider's raw error message, if any. Suitable for logging, not necessarily for end-user display.</summary>
    public string? ProviderErrorMessage { get; }

    public BhenguPaymentException(
        string providerName,
        string message,
        string? providerErrorCode = null,
        string? providerErrorMessage = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
        ProviderErrorCode = providerErrorCode;
        ProviderErrorMessage = providerErrorMessage;
    }
}
