// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core;

/// <summary>
/// Bit-flags describing what a provider supports at runtime. Consumers can query
/// <c>provider.Capabilities.HasFlag(ProviderCapabilities.Refund)</c> to decide whether to
/// offer a refund button without having to read the provider's source or docs.
/// </summary>
[Flags]
public enum ProviderCapabilities
{
    /// <summary>No capabilities declared. Default; treat as "do not call".</summary>
    None = 0,

    /// <summary>Can charge a payment via <c>ProcessPaymentAsync</c>.</summary>
    Charge = 1 << 0,

    /// <summary>Can refund a previous charge via <c>ProcessRefundAsync</c> — programmatically, not just via merchant portal.</summary>
    Refund = 1 << 1,

    /// <summary>Implements <c>IPayoutProvider</c> for disbursements to a destination.</summary>
    Payout = 1 << 2,

    /// <summary>Verifies inbound webhook payloads cryptographically via <c>VerifyWebhookSignature</c>.</summary>
    Webhook = 1 << 3,

    /// <summary>Settlement is synchronous — <c>ProcessPaymentAsync</c> returns a final <see cref="Models.PaymentStatus"/> (Completed/Failed) directly. If unset, expect Pending then a webhook upgrade.</summary>
    SyncSettlement = 1 << 4,

    /// <summary>The success response includes a redirect URL the consumer must send the payer to (carried on <c>PaymentResponse.RedirectUrl</c>).</summary>
    RedirectFlow = 1 << 5,

    /// <summary>Provider tokenises only — settlement is performed by a downstream processor (Apple Pay, Google Pay).</summary>
    Tokeniser = 1 << 6,

    /// <summary>Supports cross-border (multi-currency) transactions natively.</summary>
    CrossBorder = 1 << 7,

    /// <summary>Supports mobile-money payment methods (MSISDN-keyed wallets).</summary>
    MobileMoney = 1 << 8,

    /// <summary>Supports card payment methods (Visa/Mastercard/local card schemes).</summary>
    Cards = 1 << 9,

    /// <summary>Supports bank account-to-account transfers (EFT / instant clearing / open banking).</summary>
    BankTransfer = 1 << 10,

    /// <summary>Implements <c>ITokenisationProvider</c> — can vault card / payment-method details for re-use.</summary>
    Tokenisation = 1 << 11,

    /// <summary>Implements <c>ISubscriptionProvider</c> — supports plans and recurring billing cycles.</summary>
    Subscriptions = 1 << 12,

    /// <summary>Implements <c>IThreeDSecureProvider</c> — exposes a 3DS / SCA challenge step-up flow.</summary>
    ThreeDSecure = 1 << 13,

    /// <summary>Implements <c>IDisputeProvider</c> — exposes chargeback / dispute lifecycle.</summary>
    Disputes = 1 << 14,

    /// <summary>Implements <c>ISettlementProvider</c> — exposes settlement batches / reconciliation feeds.</summary>
    Settlement = 1 << 15,

    /// <summary>Implements <c>IQrCodeProvider</c> — can generate static or dynamic QR payment codes.</summary>
    QrCode = 1 << 16,

    /// <summary>Implements <c>IMandateProvider</c> — supports debit-order / pull-payment mandates.</summary>
    Mandates = 1 << 17,

    /// <summary>Implements <c>IMarketplaceProvider</c> — supports split payments and sub-merchant accounts.</summary>
    Marketplace = 1 << 18,

    /// <summary>Honours per-request <c>IdempotencyKey</c> — safe to retry on transient failure.</summary>
    Idempotency = 1 << 19,

    /// <summary>Returns strongly-typed <see cref="Models.Webhooks.WebhookEventCategory"/> events from <c>ParseWebhookAsync</c>.</summary>
    TypedWebhooks = 1 << 20,

    /// <summary>Accepts partial refunds where the refund amount is less than the original charge.</summary>
    PartialRefund = 1 << 21
}
