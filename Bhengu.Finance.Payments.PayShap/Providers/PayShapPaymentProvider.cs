// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.PayShap.Providers;

/// <summary>
/// PayShap adapter that satisfies the generic <see cref="IPaymentGatewayProvider"/> contract so PayShap
/// can appear alongside other gateways in a provider factory.
/// <para>
/// PayShap is a real-time bank-transfer rail rather than a card gateway, so most of its capabilities
/// (proxy resolution, RTC, EFT, account verification) don't map cleanly onto the generic contract.
/// Consumers that need PayShap functionality should inject <see cref="Services.Interfaces.IPayShapService"/>
/// directly. This adapter exists so PayShap can be listed by name in cross-provider tooling.
/// </para>
/// </summary>
public sealed class PayShapPaymentProvider : IPaymentGatewayProvider
{
    private readonly ILogger<PayShapPaymentProvider> _logger;

    public string ProviderName => "payshap";

    public PayShapPaymentProvider(ILogger<PayShapPaymentProvider> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default) =>
        throw NotMapped(nameof(ProcessPaymentAsync),
            "Use IPayShapService.InitiateRtcPaymentAsync (real-time clearing) or InitiatePaymentAsync (delayed clearing).");

    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default) =>
        throw NotMapped(nameof(ProcessRefundAsync),
            "PayShap has no refund concept. To reverse a payment, initiate a new PayShap transfer in the opposite direction.");

    public bool VerifyWebhookSignature(string payload, string signature) =>
        throw NotMapped(nameof(VerifyWebhookSignature),
            "Use IPayShapEventHandler / PayShapSignatureHelper for inbound event verification.");

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) =>
        throw NotMapped(nameof(ParseWebhookAsync),
            "Use IPayShapEventHandler.HandleEventAsync — PayShap has typed event payloads under Models/Events.");

    private BhenguPaymentException NotMapped(string member, string guidance) => new(
        ProviderName,
        $"PayShap does not map to {member} on the generic IPaymentGatewayProvider. {guidance}");
}
