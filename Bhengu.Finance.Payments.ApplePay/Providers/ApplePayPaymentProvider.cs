// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ApplePay.Configuration;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ApplePay.Providers;

/// <summary>
/// Apple Pay provider scaffold. The interface conforms to IPaymentGatewayProvider so consumers
/// can wire this in alongside other providers, but the implementation throws until the Apple Pay
/// merchant onboarding (certificate, domain validation, payment processor selection) is complete.
/// Track completion via the README in this package.
/// </summary>
public sealed class ApplePayPaymentProvider : IPaymentGatewayProvider
{
    private readonly ApplePayOptions _options;
    private readonly ILogger<ApplePayPaymentProvider> _logger;

    public string ProviderName => "applepay";

    public ApplePayPaymentProvider(IOptions<ApplePayOptions> options, ILogger<ApplePayPaymentProvider> logger)
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
        $"Apple Pay {member} is a scaffold pending merchant onboarding. " +
        $"Complete Apple Pay setup (merchant ID, certificate, domain validation) and provide a real implementation.");
}
