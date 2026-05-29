// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Exceptions;

/// <summary>
/// A webhook payload's signature did not verify against the configured secret.
/// Treat any callback raising this as untrusted.
/// </summary>
public sealed class WebhookSignatureException : BhenguPaymentException
{
    public WebhookSignatureException(
        string providerName,
        string? providerErrorMessage = null,
        Exception? innerException = null)
        : base(
            providerName,
            $"Webhook signature verification failed for {providerName}",
            providerErrorCode: null,
            providerErrorMessage,
            innerException)
    {
    }
}
