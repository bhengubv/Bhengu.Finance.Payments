// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Mukuru.Providers;

/// <summary>
/// MukuruPay collection provider. MukuruPay is a <b>PayFast payment method</b>: the buyer is shown a code
/// they take to any Mukuru branch to pay in cash. This provider therefore <b>delegates to PayFast</b> —
/// <see cref="ProcessPaymentAsync"/> builds a PayFast checkout redirect (where the buyer selects MukuruPay),
/// and refunds + webhooks delegate to the PayFast provider. Enable MukuruPay on your PayFast account and
/// register PayFast (<c>AddPayFastPayments</c>) alongside this provider.
/// <para>
/// (Mukuru's separate B2B remittance API is not publicly consumable; this provider intentionally covers
/// only the real, reachable path: MukuruPay collection via PayFast.)
/// </para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "MukuruPay via PayFast; DocsOnly until verified against a live PayFast account with MukuruPay enabled.")]
public sealed class MukuruPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly PayFastFormBuilder _formBuilder;
    private readonly PayFastPaymentProvider _payFast;
    private readonly MukuruOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Mukuru;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow;

    /// <summary>Construct the provider. Requires PayFast to be registered (it delegates to it). Designed for DI.</summary>
    public MukuruPaymentProvider(
        PayFastFormBuilder formBuilder,
        PayFastPaymentProvider payFast,
        IOptions<MukuruOptions> options,
        ILogger<MukuruPaymentProvider> logger)
        : base(logger)
    {
        _formBuilder = formBuilder ?? throw new ArgumentNullException(nameof(formBuilder));
        _payFast = payFast ?? throw new ArgumentNullException(nameof(payFast));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () =>
        {
            var mPaymentId = request.IdempotencyKey ?? request.PaymentMethodToken ?? $"mukuru-{Guid.NewGuid():N}";
            var url = _formBuilder.BuildOnceOffPaymentUrl(
                mPaymentId: mPaymentId,
                amount: request.Amount,
                itemName: string.IsNullOrWhiteSpace(request.Description) ? _options.DefaultItemName : request.Description,
                description: request.Description,
                emailAddress: request.Metadata?.GetValueOrDefault("email"),
                nameFirst: request.Metadata?.GetValueOrDefault("name"),
                cellNumber: request.Metadata?.GetValueOrDefault("msisdn"),
                currency: request.Currency.ToUpperInvariant());

            Logger.LogInformation("MukuruPay via PayFast: m_payment_id={Ref} amount={Amount} {Currency}",
                mPaymentId, request.Amount, request.Currency);

            return Task.FromResult(new PaymentResponse
            {
                GatewayReference = mPaymentId,
                Status = PaymentStatus.Pending,   // buyer completes on the PayFast checkout (selecting MukuruPay)
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                RedirectUrl = url
            });
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
        => _payFast.ProcessRefundAsync(request, ct);

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
        => _payFast.VerifyWebhookSignature(payload, signature);

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
        => _payFast.ParseWebhookAsync(payload, ct);
}
