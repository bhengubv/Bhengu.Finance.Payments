// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Exceptions;

/// <summary>
/// The provider returned a rate-limit response (typically HTTP 429).
/// Consumers should back off and retry per the provider's Retry-After hint (if any).
/// </summary>
public sealed class ProviderRateLimitException : BhenguPaymentException
{
    /// <summary>Seconds the provider asks the caller to wait before retrying, if supplied.</summary>
    public int? RetryAfterSeconds { get; }

    public ProviderRateLimitException(
        string providerName,
        int? retryAfterSeconds = null,
        string? providerErrorMessage = null,
        Exception? innerException = null)
        : base(
            providerName,
            $"{providerName} rate-limited the request" + (retryAfterSeconds is null ? "" : $" (retry after {retryAfterSeconds}s)"),
            providerErrorCode: "rate_limited",
            providerErrorMessage,
            innerException)
    {
        RetryAfterSeconds = retryAfterSeconds;
    }
}
