// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.ChipperCash.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ChipperCash.Providers;

/// <summary>
/// Chipper Cash saved-recipient READ adapter (<see cref="ITokenisationProvider"/>) — RESERVED SCAFFOLD.
/// Chipper's Network API is gated to onboarded merchants (see <see cref="ChipperCashPaymentProvider"/>);
/// this throws on use until the real spec is obtained.
/// </summary>
public sealed class ChipperCashTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.ChipperCash;

    /// <summary>Construct the reserved scaffold. Designed to be registered via DI.</summary>
    public ChipperCashTokenisationProvider(HttpClient httpClient, IOptions<ChipperCashOptions> options, ILogger<ChipperCashTokenisationProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _ = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc/>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
        => throw new BhenguPaymentException(ProviderName, ChipperCashScaffold.NotAvailable, "not_available");

    /// <inheritdoc/>
    public IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
        => throw new BhenguPaymentException(ProviderName, ChipperCashScaffold.NotAvailable, "not_available");

    /// <inheritdoc/>
    public Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
        => throw new BhenguPaymentException(ProviderName, ChipperCashScaffold.NotAvailable, "not_available");
}

/// <summary>
/// Chipper Cash saved-recipient WRITE adapter (<see cref="IRawCardTokenisationProvider"/>) — RESERVED SCAFFOLD.
/// See <see cref="ChipperCashPaymentProvider"/>.
/// </summary>
public sealed class ChipperCashRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.ChipperCash;

    /// <summary>Construct the reserved scaffold. Designed to be registered via DI.</summary>
    public ChipperCashRawCardTokenisationProvider(
        HttpClient httpClient, IOptions<ChipperCashOptions> options, ILogger<ChipperCashRawCardTokenisationProvider> logger, IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _ = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _ = cache;
    }

    /// <inheritdoc/>
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
        => throw new BhenguPaymentException(ProviderName, ChipperCashScaffold.NotAvailable, "not_available");
}

internal static class ChipperCashScaffold
{
    public const string NotAvailable =
        "Chipper Cash Network API integration is pending merchant onboarding. Chipper's Network API spec is " +
        "gated — apply for access via chippercash.com/api (\"Get Started\") or networkapi@chippercash.com, then " +
        "implement against the real spec. This provider is a reserved scaffold and makes no live calls.";
}
