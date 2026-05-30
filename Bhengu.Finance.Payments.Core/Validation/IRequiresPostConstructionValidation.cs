// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Validation;

/// <summary>
/// Opt-in interface for providers that need cross-provider validation AFTER the DI container has
/// fully built (e.g. Apple Pay / Google Pay need to verify that their configured downstream
/// processor is also registered). The <see cref="BhenguPaymentStartupValidator"/> calls
/// <see cref="Validate"/> on any provider that implements this — at app startup, before the
/// first request.
/// </summary>
public interface IRequiresPostConstructionValidation
{
    /// <summary>Throw <see cref="Exceptions.ProviderConfigurationException"/> if the provider is misconfigured.</summary>
    void Validate();
}
