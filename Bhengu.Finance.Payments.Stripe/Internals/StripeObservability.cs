// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Bhengu.Finance.Payments.Stripe.Internals;

/// <summary>
/// Shared Stripe-exception translator that classifies the Stripe.net <see cref="StripeException"/>
/// into the canonical Bhengu exception hierarchy. Used by every Stripe provider so the outcome
/// classifier in <c>BhenguProviderBase</c> records the right tag (<c>declined</c> /
/// <c>rate_limited</c> / <c>unavailable</c> / <c>error</c>).
/// </summary>
internal static class StripeExceptionTranslator
{
    /// <summary>
    /// Translate a Stripe SDK exception into the canonical <see cref="BhenguPaymentException"/>
    /// hierarchy. Logs the underlying HTTP status + error metadata at error level.
    /// </summary>
    public static BhenguPaymentException Translate(
        StripeException ex,
        string providerName,
        string operation,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(ex);
        ArgumentNullException.ThrowIfNull(logger);

        var httpStatus = (int)ex.HttpStatusCode;
        var errorCode = ex.StripeError?.Code ?? ex.HttpStatusCode.ToString();
        var errorMessage = ex.StripeError?.Message ?? ex.Message;

        logger.LogError(ex, "Stripe {Operation} failed: {HttpStatus} {Code} {Message}",
            operation, httpStatus, errorCode, errorMessage);

        if (httpStatus == 429)
            return new ProviderRateLimitException(providerName, providerErrorMessage: errorMessage, innerException: ex);

        if (httpStatus is >= 400 and < 500)
            return new PaymentDeclinedException(providerName, errorCode, errorMessage, ex);

        return new ProviderUnavailableException(providerName, $"HTTP {httpStatus}: {errorMessage}", ex);
    }
}
