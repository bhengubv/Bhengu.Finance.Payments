// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// The core contract every payment-gateway provider implements.
/// Implementations live in their own SDK projects (e.g. <c>Bhengu.Finance.Payments.PayFast</c>).
/// </summary>
public interface IPaymentGatewayProvider
{
    /// <summary>Canonical short name for the provider — e.g. "payfast", "stripe", "bricspay". Use <see cref="ProviderNames"/> constants instead of bare strings.</summary>
    string ProviderName { get; }

    /// <summary>
    /// What the provider supports at runtime. Consumers should check this before calling methods that
    /// might not apply — e.g. <c>if (provider.Capabilities.HasFlag(ProviderCapabilities.Refund)) ...</c>.
    /// Removes the need to read each provider's source/docs to discover its surface.
    /// </summary>
    ProviderCapabilities Capabilities { get; }

    /// <summary>Charge a tokenised payment method.</summary>
    /// <exception cref="Exceptions.PaymentDeclinedException">Provider declined the payment.</exception>
    /// <exception cref="Exceptions.ProviderRateLimitException">Provider rate-limited the request.</exception>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable or returned a server error.</exception>
    Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default);

    /// <summary>Refund a previously-completed payment, partially or in full.</summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    /// <exception cref="Exceptions.BhenguPaymentException">Refund could not be processed (provider rejected, transaction not refundable, etc).</exception>
    Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default);

    /// <summary>Verify a webhook payload's signature against the configured secret. Pure function; no I/O.</summary>
    bool VerifyWebhookSignature(string payload, string signature);

    /// <summary>Parse a provider webhook payload into a normalised <see cref="WebhookEvent"/>.</summary>
    /// <returns>The parsed event, or <c>null</c> if the payload is not a transaction event the SDK recognises.</returns>
    Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default);
}
