// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Models.Marketplace;

/// <summary>
/// A request to onboard a new sub-merchant account.
/// </summary>
public sealed record SubAccountRequest
{
    /// <summary>Business / individual name.</summary>
    public required string BusinessName { get; init; }

    /// <summary>Contact email used for notifications and onboarding.</summary>
    public required string ContactEmail { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code of the sub-merchant.</summary>
    public required string Country { get; init; }

    /// <summary>Default settlement currency (ISO 4217). Some providers infer from country.</summary>
    public string? SettlementCurrency { get; init; }

    /// <summary>Settlement bank-account token. Optional when the provider's hosted onboarding collects it.</summary>
    public string? SettlementAccountToken { get; init; }

    /// <summary>URL to return the sub-merchant to after hosted onboarding completes.</summary>
    public string? ReturnUrl { get; init; }

    /// <summary>Caller-supplied idempotency key.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Provider-specific extension fields (e.g. business_type, MCC code).</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}
