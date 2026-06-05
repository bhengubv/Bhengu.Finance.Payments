// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Core.Validation;

/// <summary>
/// Hosted service that eagerly resolves every registered <see cref="IPaymentGatewayProvider"/> at
/// application startup. This triggers each provider's constructor to run, surfacing any
/// <see cref="Exceptions.ProviderConfigurationException"/> (missing options, missing downstream
/// processor for Apple Pay / Google Pay, etc.) BEFORE the app starts serving traffic — instead of
/// on the first inbound request.
///
/// <para>When <see cref="BhenguPaymentStartupValidationOptions.RequireVerifiedProviders"/> is
/// enabled, the validator also reads each provider's <see cref="ProviderVerificationStatusAttribute"/>
/// and refuses to start if any registered provider is marked <see cref="ProviderVerificationStatus.DocsOnly"/>
/// (unless the consumer has explicitly allowlisted it via
/// <see cref="BhenguPaymentStartupValidationOptions.AllowUnverifiedProviders"/>).</para>
///
/// <para>Registered automatically by every <c>AddXxxPayments</c> extension via
/// <see cref="ServiceCollectionExtensions.AddBhenguPaymentStartupValidation"/>.</para>
/// </summary>
public sealed class BhenguPaymentStartupValidator : IHostedService
{
    private readonly IEnumerable<IPaymentGatewayProvider> _providers;
    private readonly BhenguPaymentStartupValidationOptions _options;
    private readonly ILogger<BhenguPaymentStartupValidator> _logger;

    /// <summary>Construct with DI-injected providers + options + logger.</summary>
    public BhenguPaymentStartupValidator(
        IEnumerable<IPaymentGatewayProvider> providers,
        IOptions<BhenguPaymentStartupValidationOptions> options,
        ILogger<BhenguPaymentStartupValidator> logger)
    {
        _providers = providers;
        _options = options?.Value ?? new BhenguPaymentStartupValidationOptions();
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var names = new List<string>();
        var providerList = _providers.ToList(); // materialise once so we can do two passes
        foreach (var provider in providerList)
        {
            // Touching ProviderName forces the instance to materialise. If the constructor throws
            // ProviderConfigurationException, it'll bubble up here and crash the app at startup —
            // which is what we want (fail fast, not at first request).
            names.Add(provider.ProviderName);
        }

        // Second pass: any provider that needs cross-provider validation (Apple Pay / Google Pay
        // checking their downstream is registered) runs Validate() here, with the full provider
        // list now materialised.
        foreach (var provider in providerList)
        {
            if (provider is IRequiresPostConstructionValidation validatable)
                validatable.Validate();
        }

        // Third pass: if RequireVerifiedProviders is on, refuse to start when any provider is
        // DocsOnly and not on the allow-list. Protects production deployments from accidentally
        // shipping a provider whose wire format hasn't been sandbox-verified.
        if (_options.RequireVerifiedProviders)
        {
            var unverified = new List<string>();
            foreach (var provider in providerList)
            {
                var attr = provider.GetType().GetCustomAttributes(
                    typeof(ProviderVerificationStatusAttribute), inherit: false)
                    .OfType<ProviderVerificationStatusAttribute>()
                    .FirstOrDefault();
                var status = attr?.Status ?? ProviderVerificationStatus.DocsOnly;

                if (status == ProviderVerificationStatus.DocsOnly &&
                    !_options.AllowUnverifiedProviders.Contains(provider.ProviderName, StringComparer.OrdinalIgnoreCase))
                {
                    unverified.Add(provider.ProviderName);
                }
            }

            if (unverified.Count > 0)
            {
                throw new ProviderConfigurationException(
                    string.Join(",", unverified),
                    $"RequireVerifiedProviders is enabled but the following provider(s) are marked DocsOnly " +
                    $"(wire format built from public documentation, never sandbox-verified): {string.Join(", ", unverified)}. " +
                    $"Either verify each against the provider's sandbox and submit a PR upgrading the " +
                    $"[ProviderVerificationStatus] attribute, or explicitly allowlist them via " +
                    $"BhenguPaymentStartupValidationOptions.AllowUnverifiedProviders (acknowledges the risk).");
            }
        }

        _logger.LogInformation(
            "Bhengu.Finance.Payments startup validation passed. {Count} provider(s) ready: {Names}",
            names.Count, string.Join(", ", names));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Bind from <c>Bhengu:Finance:Payments:StartupValidation</c>. Defaults are conservative
/// (warning-only) so existing callers don't break.
/// </summary>
public sealed class BhenguPaymentStartupValidationOptions
{
    /// <summary>Configuration section name.</summary>
    public const string ConfigSection = "Bhengu:Finance:Payments:StartupValidation";

    /// <summary>
    /// When true, startup fails if any registered provider is <see cref="ProviderVerificationStatus.DocsOnly"/>
    /// unless explicitly listed in <see cref="AllowUnverifiedProviders"/>. Default false — opt-in
    /// for production deployments that want belt-and-braces protection against shipping unverified
    /// wire format.
    /// </summary>
    public bool RequireVerifiedProviders { get; set; } = false;

    /// <summary>
    /// Provider names (canonical, see <see cref="ProviderNames"/>) for which the consumer
    /// explicitly accepts <see cref="ProviderVerificationStatus.DocsOnly"/>. Only consulted when
    /// <see cref="RequireVerifiedProviders"/> is true.
    /// </summary>
    public IList<string> AllowUnverifiedProviders { get; set; } = new List<string>();
}
