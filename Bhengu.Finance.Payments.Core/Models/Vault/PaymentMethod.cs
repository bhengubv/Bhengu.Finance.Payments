// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Vault;

/// <summary>
/// A vaulted payment method — the result of tokenising raw card / bank / wallet details.
/// Safe to persist server-side; the SDK only ever exposes the token plus non-sensitive descriptors.
/// </summary>
public sealed record PaymentMethod
{
    /// <summary>The provider's token. Pass as <c>PaymentRequest.PaymentMethodToken</c> on subsequent charges.</summary>
    public required string Token { get; init; }

    /// <summary>The vault customer this payment method belongs to.</summary>
    public string? CustomerId { get; init; }

    /// <summary>What kind of method this is — used by UIs to render the right icon.</summary>
    public required PaymentMethodKind Kind { get; init; }

    /// <summary>For cards: brand (e.g. "visa", "mastercard", "amex"). For wallets: wallet name. Null otherwise.</summary>
    public string? Brand { get; init; }

    /// <summary>For cards: last 4 digits of the PAN. For bank accounts: last 4 of the account number. Null otherwise.</summary>
    public string? Last4 { get; init; }

    /// <summary>For cards: expiry month, 1-12. Null otherwise.</summary>
    public int? ExpiryMonth { get; init; }

    /// <summary>For cards: 4-digit expiry year. Null otherwise.</summary>
    public int? ExpiryYear { get; init; }

    /// <summary>Optional display label set by the payer or merchant (e.g. "Work card").</summary>
    public string? DisplayName { get; init; }

    /// <summary>True when this is the customer's default payment method.</summary>
    public bool IsDefault { get; init; }

    /// <summary>UTC timestamp the method was added to the vault.</summary>
    public DateTime? CreatedAt { get; init; }
}
