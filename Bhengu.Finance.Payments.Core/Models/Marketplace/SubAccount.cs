// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Marketplace;

/// <summary>
/// A sub-merchant account inside the platform's marketplace.
/// </summary>
public sealed record SubAccount
{
    /// <summary>The sub-account's gateway reference. Use on <see cref="SplitRule.SubAccountReference"/>.</summary>
    public required string Reference { get; init; }

    /// <summary>Business / individual name.</summary>
    public required string BusinessName { get; init; }

    /// <summary>Contact email.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>Settlement bank account / wallet token where this sub-merchant receives funds.</summary>
    public string? SettlementAccountToken { get; init; }

    /// <summary>True when the sub-account is fully onboarded and able to receive funds.</summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Where the sub-merchant must complete onboarding (KYC, bank verification). Set on first
    /// creation by providers that use a hosted onboarding flow (Stripe Connect Express, Paystack
    /// Subaccount onboarding). Null once onboarding is complete.
    /// </summary>
    public string? OnboardingUrl { get; init; }
}
