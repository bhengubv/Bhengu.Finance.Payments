// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Xunit;

namespace Bhengu.Finance.Payments.Tests.Stripe;

/// <summary>
/// xunit collection definition that serialises every Stripe test class which mutates the
/// process-wide <c>Stripe.StripeConfiguration.ApiKey</c> static. Test classes that read or
/// assert on that static must declare <c>[Collection(StripeConfigurationCollection.Name)]</c>
/// so they don't observe a key written by a sibling test class running in parallel.
///
/// <para>Without this serialisation, <c>StripePaymentProviderTests.Constructor_SetsStripeConfigurationApiKey</c>
/// was flaky — a constructor in <c>StripeMandateProviderTests</c> (etc.) running in parallel
/// would overwrite the ApiKey between this test's write and its assertion.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StripeConfigurationCollection
{
    /// <summary>The collection name. Apply to test classes via <c>[Collection(StripeConfigurationCollection.Name)]</c>.</summary>
    public const string Name = "StripeConfiguration";
}
