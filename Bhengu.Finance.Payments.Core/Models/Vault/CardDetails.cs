// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Configuration;

namespace Bhengu.Finance.Payments.Core.Models.Vault;

/// <summary>
/// Raw card details supplied to <see cref="Interfaces.IRawCardTokenisationProvider.TokeniseAsync"/>.
/// This object MUST be discarded immediately after tokenisation — never log, never persist,
/// never serialise.
///
/// <para>The compiler-defended pieces:</para>
/// <list type="bullet">
///   <item><see cref="ToString"/> redacts to last-4-digits-of-PAN so accidental string interpolation never leaks.</item>
///   <item><see cref="ToRedactedString"/> provides the same shape for <see cref="IRedactable"/>-aware loggers.</item>
///   <item><see cref="JsonConverterAttribute"/> binds <see cref="CardDetailsJsonConverter"/> which THROWS on serialise. Any structured-logging path that tries to JSON-serialise a CardDetails (Seq, Serilog with JSON sink, ApplicationInsights) gets a loud exception at write-time instead of silently shipping PAN to an indexed store.</item>
/// </list>
/// </summary>
/// <remarks>
/// For PCI-DSS compliance most merchants should NOT handle raw PAN — instead use a hosted-field
/// SDK on the client side (Stripe Elements, Razorpay Standard Checkout, etc.) that returns a
/// short-lived token to your server. This DTO is provided for server-side tokenisation flows
/// where the merchant is already SAQ-D scoped or for non-card payment methods.
/// </remarks>
[JsonConverter(typeof(CardDetailsJsonConverter))]
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

    /// <summary>Redacted shape — masks PAN to last 4, hides CVV. Override of <see cref="object.ToString"/> guards against accidental string-interpolation leaks.</summary>
    public override string ToString() => ToRedactedString();

    /// <summary>Returns a redacted representation safe for logging — masks PAN to last 4, hides CVV entirely.</summary>
    public string ToRedactedString()
    {
        var last4 = CardNumber is { Length: >= 4 } pan ? pan[^4..] : "****";
        return $"CardDetails(holder=***, pan=****-****-****-{last4}, exp={ExpiryMonth:D2}/{ExpiryYear}, cvv=***)";
    }
}

/// <summary>
/// <see cref="JsonConverter"/> that THROWS on serialisation. This is intentional: structured-
/// logging frameworks (Serilog JSON sink, ApplicationInsights, Seq) that try to JSON-serialise a
/// <see cref="CardDetails"/> would otherwise silently ship PAN to an indexed search store —
/// PCI-DSS req 3 violation. We make that impossible by failing loud.
///
/// <para>Read is permitted because providers genuinely need to deserialise card details when
/// they arrive from the caller — but no SDK code path ever serialises CardDetails back out.</para>
/// </summary>
public sealed class CardDetailsJsonConverter : JsonConverter<CardDetails>
{
    /// <inheritdoc />
    public override CardDetails? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Standard deserialisation — providers need to hydrate from the caller's JSON.
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        return new CardDetails
        {
            CardholderName = root.GetProperty("cardholderName").GetString() ?? string.Empty,
            CardNumber = root.GetProperty("cardNumber").GetString() ?? string.Empty,
            ExpiryMonth = root.GetProperty("expiryMonth").GetInt32(),
            ExpiryYear = root.GetProperty("expiryYear").GetInt32(),
            Cvv = root.TryGetProperty("cvv", out var cvv) ? cvv.GetString() : null,
            BillingAddressLine1 = root.TryGetProperty("billingAddressLine1", out var bl1) ? bl1.GetString() : null,
            BillingPostalCode = root.TryGetProperty("billingPostalCode", out var bpc) ? bpc.GetString() : null,
            BillingCountry = root.TryGetProperty("billingCountry", out var bc) ? bc.GetString() : null,
        };
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, CardDetails value, JsonSerializerOptions options)
    {
        throw new InvalidOperationException(
            "Refusing to JSON-serialise CardDetails — raw PAN must never leave the tokenisation request. " +
            "If you reached this exception via a logging framework, that's the bug we're guarding against: " +
            "PCI-DSS requirement 3 forbids storing PAN in indexed structured logs. " +
            "Use CardDetails.ToRedactedString() for diagnostic output.");
    }
}
