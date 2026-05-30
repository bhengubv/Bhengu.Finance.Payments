// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Core.Validation;

/// <summary>
/// Hosted service that eagerly resolves every registered <see cref="IPaymentGatewayProvider"/> at
/// application startup. This triggers each provider's constructor to run, surfacing any
/// <see cref="Exceptions.ProviderConfigurationException"/> (missing options, missing downstream
/// processor for Apple Pay / Google Pay, etc.) BEFORE the app starts serving traffic — instead of
/// on the first inbound request.
/// <para>
/// Registered automatically by every <c>AddXxxPayments</c> extension via
/// <see cref="ServiceCollectionExtensions.AddBhenguPaymentStartupValidation"/>.
/// </para>
/// </summary>
public sealed class BhenguPaymentStartupValidator : IHostedService
{
    private readonly IEnumerable<IPaymentGatewayProvider> _providers;
    private readonly ILogger<BhenguPaymentStartupValidator> _logger;

    public BhenguPaymentStartupValidator(
        IEnumerable<IPaymentGatewayProvider> providers,
        ILogger<BhenguPaymentStartupValidator> logger)
    {
        _providers = providers;
        _logger = logger;
    }

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

        _logger.LogInformation(
            "Bhengu.Finance.Payments startup validation passed. {Count} provider(s) ready: {Names}",
            names.Count, string.Join(", ", names));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
