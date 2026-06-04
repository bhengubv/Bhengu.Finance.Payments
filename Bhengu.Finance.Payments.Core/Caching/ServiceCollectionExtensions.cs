// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bhengu.Finance.Payments.Core.Caching;

/// <summary>
/// DI registration helpers for <see cref="IBhenguDistributedCache"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the process-local <see cref="InMemoryBhenguDistributedCache"/> as the default
    /// <see cref="IBhenguDistributedCache"/> implementation. Idempotent — repeated calls are no-ops.
    ///
    /// <para>Provider <c>Add*Payments</c> extensions call this internally so consumers don't have
    /// to remember it; explicit consumers can call it before <c>AddBhenguRedisCache</c> if they
    /// want to start in-memory then swap.</para>
    /// </summary>
    public static IServiceCollection AddBhenguInMemoryCache(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IBhenguDistributedCache, InMemoryBhenguDistributedCache>();
        return services;
    }
}
