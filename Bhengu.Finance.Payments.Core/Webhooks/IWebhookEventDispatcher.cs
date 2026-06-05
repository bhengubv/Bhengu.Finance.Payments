// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;

namespace Bhengu.Finance.Payments.Core.Webhooks;

/// <summary>
/// Safe-by-default webhook entry point. Replaces the manual sequence
/// (<c>VerifyWebhookSignature</c> → <c>ParseWebhookAsync</c> → handler invoke) with a single call
/// that ALSO records the verified envelope to <see cref="IWebhookReplayStore"/> for dedup +
/// re-delivery. Consumers can't accidentally skip the dedup step.
///
/// <para>Recommended consumer pattern:</para>
/// <code>
/// app.MapPost("/webhooks/{provider}", async (string provider, HttpRequest req, IWebhookEventDispatcher d) =&gt;
/// {
///     using var reader = new StreamReader(req.Body);
///     var payload = await reader.ReadToEndAsync();
///     var signature = req.Headers["X-Provider-Signature"].ToString();
///     var outcome = await d.DispatchAsync(provider, payload, signature, async evt =&gt;
///     {
///         // your business handler — only ever called ONCE per unique event id even under
///         // provider redelivery storms
///     });
///     return outcome.ToHttpResult();
/// });
/// </code>
/// </summary>
public interface IWebhookEventDispatcher
{
    /// <summary>
    /// Verify the signature → parse → dedup-check via <see cref="IWebhookReplayStore"/> →
    /// invoke <paramref name="handler"/> at most once → mark handled/failed in the store.
    /// </summary>
    /// <param name="providerName">Canonical provider name (use <see cref="ProviderNames"/>).</param>
    /// <param name="rawPayload">Raw request body bytes as the provider sent them.</param>
    /// <param name="signatureHeader">The signature header value (provider-specific).</param>
    /// <param name="handler">Business handler — invoked at most once per unique event.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<WebhookDispatchOutcome> DispatchAsync(
        string providerName,
        string rawPayload,
        string signatureHeader,
        Func<WebhookEvent, CancellationToken, Task> handler,
        CancellationToken ct = default);
}

/// <summary>What happened when an inbound webhook was dispatched.</summary>
public enum WebhookDispatchOutcome
{
    /// <summary>Signature verified, event parsed, handler invoked successfully.</summary>
    Handled,
    /// <summary>Signature failed verification. Reject with 401 — DO NOT retry.</summary>
    InvalidSignature,
    /// <summary>The provider sent a payload type the SDK doesn't recognise. Acknowledge with 200 to stop redelivery.</summary>
    UnknownEvent,
    /// <summary>This event id has already been handled. Acknowledge with 200 — dedup worked.</summary>
    Duplicate,
    /// <summary>Handler threw. Returned 500 for provider to retry; envelope captured in store for diagnostic replay.</summary>
    HandlerFailed,
    /// <summary>No provider registered for the supplied name. 400.</summary>
    UnknownProvider,
}
