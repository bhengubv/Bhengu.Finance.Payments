// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.JamboPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.JamboPay.Providers;

/// <summary>
/// JamboPay (Kenya, Web Tribe Ltd) payment provider — RESERVED SCAFFOLD.
/// <para>
/// JamboPay's public API documentation is offline: <c>apidocs.jambopay.co.ke</c> errors and the dev
/// portal <c>backoffice.jambopay.com</c> does not resolve (NXDOMAIN). With no reachable spec, this
/// provider throws on use rather than shipping a guessed wire format. Rebuild against the real API once
/// it is reachable.
/// </para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Reserved scaffold — JamboPay's public API docs are offline; no reachable spec.")]
public sealed class JamboPayPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private const string NotAvailable =
        "JamboPay integration is unavailable: its public API documentation is offline (apidocs.jambopay.co.ke " +
        "and the backoffice.jambopay.com dev portal do not resolve). This provider is a reserved scaffold and " +
        "makes no live calls — rebuild against the real API once it is reachable.";

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.JamboPay;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    /// <summary>Construct the reserved scaffold. Designed to be registered via DI.</summary>
    public JamboPayPaymentProvider(
        HttpClient httpClient,
        IOptions<JamboPayOptions> options,
        ILogger<JamboPayPaymentProvider> logger,
        IBhenguDistributedCache? idempotencyCache = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _ = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _ = idempotencyCache;
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
        => throw new BhenguPaymentException(ProviderName, NotAvailable, "not_available");

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
        => throw new BhenguPaymentException(ProviderName, NotAvailable, "not_available");

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
        => throw new BhenguPaymentException(ProviderName, NotAvailable, "not_available");

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature) => false;

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
        => Task.FromResult<WebhookEvent?>(null);
}
