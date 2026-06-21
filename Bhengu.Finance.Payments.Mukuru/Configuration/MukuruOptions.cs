// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Mukuru.Configuration;

/// <summary>
/// Configuration for the MukuruPay provider. MukuruPay is a <b>PayFast payment method</b> (the buyer is
/// shown a code and pays cash at any Mukuru branch), so this provider delegates to PayFast. Configure
/// PayFast under <c>Bhengu:Finance:Payments:PayFast</c> and enable MukuruPay on your PayFast account.
/// Bound from <c>Bhengu:Finance:Payments:Mukuru</c>.
/// </summary>
public sealed class MukuruOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:Mukuru";

    /// <summary>Default item name shown on the PayFast checkout when a request supplies no description.</summary>
    public string DefaultItemName { get; set; } = "MukuruPay payment";
}
