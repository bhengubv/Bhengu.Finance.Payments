// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Configuration;

namespace Bhengu.Finance.Payments.Core.Models.Vault;

/// <summary>
/// Raw card details supplied to <see cref="Interfaces.ITokenisationProvider.TokeniseAsync"/>.
/// This object MUST be discarded immediately after tokenisation — never log, never persist,
/// never serialise. Implementations of <see cref="IRedactable"/> ensure structured logging
/// frameworks redact this if accidentally logged.
/// </summary>
/// <remarks>
/// For PCI-DSS compliance most merchants should NOT handle raw PAN — instead use a hosted-field
/// SDK on the client side (Stripe Elements, Razorpay Standard Checkout, etc.) that returns a
/// short-lived token to your server. This DTO is provided for server-side tokenisation flows
/// where the merchant is already SAQ-D scoped or for non-card payment methods.
/// </remarks>
public sealed record CardDetails : IRedactable
{
    /// <summary>Cardholder name as it appears on the card.</summary>
    public required string CardholderName { get; init; }

    /// <summary>Primary account number (PAN), digits only, no spaces.</summary>
    public required string CardNumber { get; init; }

    /// <summary>Expiry month, 1-12.</summary>
    public required int ExpiryMonth { get; init; }

    /// <summary>4-digit expiry year (e.g. 2030).</summary>
    public required int ExpiryYear { get; init; }

    /// <summary>3- or 4-digit card verification value. Some tokenisation flows accept null when the card is already vaulted.</summary>
    public string? Cvv { get; init; }

    /// <summary>
    /// Optional billing address line — required by some 3DS / AVS flows.
    /// </summary>
    public string? BillingAddressLine1 { get; init; }

    /// <summary>Optional billing postal/zip code — required by some AVS flows.</summary>
    public string? BillingPostalCode { get; init; }

    /// <summary>Optional ISO 3166-1 alpha-2 country code of the billing address.</summary>
    public string? BillingCountry { get; init; }

    /// <summary>Returns a redacted representation safe for logging — masks PAN to last 4, hides CVV entirely.</summary>
    public string ToRedactedString()
    {
        var last4 = CardNumber is { Length: >= 4 } pan ? pan[^4..] : "****";
        return $"CardDetails(holder=***, pan=****-****-****-{last4}, exp={ExpiryMonth:D2}/{ExpiryYear}, cvv=***)";
    }
}
