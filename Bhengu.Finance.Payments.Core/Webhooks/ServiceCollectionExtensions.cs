// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bhengu.Finance.Payments.Core.Webhooks;

/// <summary>
/// DI registration helpers for webhook re-delivery infrastructure.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register the default in-memory <see cref="IWebhookReplayStore"/> + <see cref="IWebhookReplayer"/>.
    /// Idempotent. For production multi-replica deployments, register a DB-backed
    /// <see cref="IWebhookReplayStore"/> implementation BEFORE calling this.
    /// </summary>
    public static IServiceCollection AddBhenguWebhookReplay(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IWebhookReplayStore, InMemoryWebhookReplayStore>();
        services.TryAddSingleton<IWebhookReplayer, WebhookReplayer>();
        services.TryAddSingleton<IWebhookEventDispatcher, WebhookEventDispatcher>();
        return services;
    }
}
