// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Google.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Google.Providers;

/// <summary>
/// Google Pay provider scaffold. Implements IPaymentGatewayProvider so consumers can wire this in
/// alongside other providers, but throws until the merchant ID + downstream processor selection
/// (Stripe / Adyen / Braintree) is wired up. Google Pay tokenises only; settlement is downstream.
/// </summary>
public sealed class GooglePayPaymentProvider : IPaymentGatewayProvider
{
    private readonly GooglePayOptions _options;
    private readonly ILogger<GooglePayPaymentProvider> _logger;

    public string ProviderName => "googlepay";

    public GooglePayPaymentProvider(IOptions<GooglePayOptions> options, ILogger<GooglePayPaymentProvider> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default) =>
        throw NotImplemented(nameof(ProcessPaymentAsync));

    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default) =>
        throw NotImplemented(nameof(ProcessRefundAsync));

    public bool VerifyWebhookSignature(string payload, string signature) =>
        throw NotImplemented(nameof(VerifyWebhookSignature));

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) =>
        throw NotImplemented(nameof(ParseWebhookAsync));

    private BhenguPaymentException NotImplemented(string member) => new(
        ProviderName,
        $"Google Pay {member} is a scaffold pending downstream-processor wiring. " +
        $"Google Pay tokenises only — pick a settlement processor (Stripe/Adyen/Braintree) and forward decrypted tokens.");
}
