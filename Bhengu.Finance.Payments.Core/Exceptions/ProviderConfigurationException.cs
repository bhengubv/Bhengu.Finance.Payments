// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Exceptions;

/// <summary>
/// The provider could not be configured because a required setting was missing or invalid.
/// Raised at DI registration time (<c>AddXxxPayments()</c>) so consumers fail fast at startup.
/// </summary>
public sealed class ProviderConfigurationException : BhenguPaymentException
{
    public ProviderConfigurationException(
        string providerName,
        string missingOrInvalidSetting,
        Exception? innerException = null)
        : base(
            providerName,
            $"{providerName} is misconfigured: {missingOrInvalidSetting}",
            providerErrorCode: "misconfigured",
            providerErrorMessage: missingOrInvalidSetting,
            innerException)
    {
    }
}
