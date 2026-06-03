// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Webhooks;

/// <summary>
/// A charge completed successfully — funds are committed to the merchant account.
/// Emitted by providers that support <see cref="ProviderCapabilities.TypedWebhooks"/>.
/// </summary>
public sealed record ChargeSucceededEvent : WebhookEvent
{
    /// <summary>The amount actually settled.</summary>
    public required decimal Amount { get; init; }

    /// <summary>ISO 4217 currency code of the settled amount.</summary>
    public required string Currency { get; init; }

    /// <summary>Provider-supplied payer identifier (e.g. Stripe customer ID, Paystack customer code). Null if anonymous.</summary>
    public string? CustomerId { get; init; }

    /// <summary>Payment-method token used (e.g. Stripe pm_..., Paystack authorisation_code). Null where the provider doesn't expose one.</summary>
    public string? PaymentMethodToken { get; init; }
}
