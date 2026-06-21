// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ChipperCash.Providers;

/// <summary>
/// Chipper Cash Network API ("Pay With Chipper") provider — RESERVED SCAFFOLD.
/// <para>
/// Chipper's Network API spec is gated to onboarded merchants: access is granted via the Chipper
/// Network application form (chippercash.com/api → "Get Started", a Typeform application) or
/// <c>networkapi@chippercash.com</c>. The one public Postman doc is offline. Until a merchant account
/// is onboarded and the real spec obtained, this provider is reserved and throws on use — rather than
/// shipping a guessed wire format.
/// </para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Reserved scaffold — Chipper Network API spec is gated to onboarded merchants; not publicly available.")]
public sealed class ChipperCashPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private const string NotAvailable =
        "Chipper Cash Network API integration is pending merchant onboarding. Chipper's Network API spec is " +
        "gated — apply for access via chippercash.com/api (\"Get Started\") or networkapi@chippercash.com, then " +
        "implement against the real spec. This provider is a reserved scaffold and makes no live calls.";

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.ChipperCash;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;

    /// <summary>Construct the reserved scaffold. Designed to be registered via DI.</summary>
    public ChipperCashPaymentProvider(
        HttpClient httpClient,
        IOptions<ChipperCashOptions> options,
        ILogger<ChipperCashPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _ = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _ = cache;
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
