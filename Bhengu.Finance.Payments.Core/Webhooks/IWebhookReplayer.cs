// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.Core.Webhooks;

/// <summary>
/// Drives re-delivery of webhook envelopes whose downstream handler previously failed.
/// Pairs with <see cref="IWebhookReplayStore"/> for persistence and the provider's
/// <c>IPaymentGatewayProvider.ParseWebhookAsync</c> for re-parsing.
/// </summary>
public interface IWebhookReplayer
{
    /// <summary>
    /// Replay all pending / failed envelopes for a given provider. Each envelope is re-parsed
    /// and handed to <paramref name="handler"/>; on success the store is marked handled, on
    /// exception it's marked failed and the attempt counter incremented.
    /// </summary>
    /// <returns>The number of envelopes successfully replayed.</returns>
    Task<int> ReplayAsync(string providerName, Func<WebhookEvent, CancellationToken, Task> handler, CancellationToken ct = default);
}
