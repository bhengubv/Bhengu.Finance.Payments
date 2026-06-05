// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Validation;

/// <summary>
/// How thoroughly a provider has been validated against the real upstream API. Stamped onto every
/// provider's main class so consumers can opt out of running un-verified providers in production
/// via <c>BhenguPaymentStartupValidator.Options.RequireVerifiedProviders</c>.
///
/// <para>This is a real maturity gauge, not marketing copy:</para>
/// <list type="bullet">
///   <item><see cref="DocsOnly"/> — the wire format was built from public documentation; the SDK
///         has unit tests proving it constructs what it thinks is the right request, but no real
///         charge has ever been put through this provider's sandbox.</item>
///   <item><see cref="SandboxVerified"/> — the SDK has integration tests that hit the provider's
///         official sandbox endpoint and confirm the request shape, signature, and webhook
///         payload are accepted.</item>
///   <item><see cref="ProductionVerified"/> — real money has moved through this provider via this
///         SDK in at least one production deployment; the maintainers know the wire format works
///         end-to-end.</item>
/// </list>
/// </summary>
public enum ProviderVerificationStatus
{
    /// <summary>Built from documentation. No real sandbox interaction tested. Use with explicit opt-in.</summary>
    DocsOnly = 0,
    /// <summary>Verified against the provider's official sandbox.</summary>
    SandboxVerified = 1,
    /// <summary>Real money moved through this provider in production.</summary>
    ProductionVerified = 2,
}

/// <summary>
/// Stamp a provider class with its <see cref="ProviderVerificationStatus"/>. Consumers can query
/// via reflection at startup (and the bundled <c>BhenguPaymentStartupValidator</c> does so when
/// <c>RequireVerifiedProviders</c> is enabled).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ProviderVerificationStatusAttribute : Attribute
{
    /// <summary>The verification level.</summary>
    public ProviderVerificationStatus Status { get; }

    /// <summary>
    /// Optional note. For <see cref="ProviderVerificationStatus.SandboxVerified"/> /
    /// <see cref="ProviderVerificationStatus.ProductionVerified"/> this should describe what
    /// was tested (e.g. "Stripe sandbox, charge + refund + webhook verify"). For
    /// <see cref="ProviderVerificationStatus.DocsOnly"/> this should cite the spec URL.
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>Stamp the verification status.</summary>
    public ProviderVerificationStatusAttribute(ProviderVerificationStatus status)
    {
        Status = status;
    }
}
