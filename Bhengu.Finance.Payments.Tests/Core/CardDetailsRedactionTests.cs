// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Compiler/runtime defences around <see cref="CardDetails"/> — PAN must not leak via
/// <see cref="object.ToString"/> nor via JSON serialisation in structured logs.
/// </summary>
public class CardDetailsRedactionTests
{
    private static CardDetails Sample() => new()
    {
        CardholderName = "T. Bengu",
        CardNumber = "4111111111111234",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Cvv = "987",
    };

    [Fact]
    public void ToStringRedactsPanToLast4()
    {
        var s = Sample().ToString();
        Assert.Contains("****-****-****-1234", s);
        Assert.DoesNotContain("4111111111111234", s);
        Assert.DoesNotContain("987", s);
        Assert.DoesNotContain("T. Bengu", s);
    }

    [Fact]
    public void ToRedactedStringMatchesToString()
    {
        var sample = Sample();
        Assert.Equal(sample.ToString(), sample.ToRedactedString());
    }

    [Fact]
    public void JsonSerializeThrowsToPreventStructuredLogLeak()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => JsonSerializer.Serialize(Sample()));
        Assert.Contains("PCI", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonDeserializeRoundTripsForLegitimateProviderUse()
    {
        // Providers DO need to deserialise CardDetails from caller JSON. Only Write is blocked.
        const string json = """
            {"cardholderName":"X","cardNumber":"5555555555554444","expiryMonth":1,"expiryYear":2030,"cvv":"111"}
            """;
        var card = JsonSerializer.Deserialize<CardDetails>(json);
        Assert.NotNull(card);
        Assert.Equal("5555555555554444", card!.CardNumber);
        Assert.Equal("X", card.CardholderName);
    }
}
