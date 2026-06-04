// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Exercises <see cref="InMemoryWebhookReplayStore"/> + <see cref="WebhookReplayer"/> end-to-end.
/// Dedup on TryRecord, ListPending ordering, MarkHandled/MarkFailed state transitions,
/// ReplayAsync provider lookup, handler-exception-marks-failed, and success counting.
/// </summary>
public class WebhookReplayTests
{
    private static WebhookEnvelope Env(string id, string provider = "stripe", string payload = "{}", int secondsAgo = 0) => new()
    {
        ProviderName = provider,
        EventId = id,
        RawPayload = payload,
        ReceivedAt = DateTime.UtcNow.AddSeconds(-secondsAgo),
        Signature = "sig",
    };

    /// <summary>Minimal IPaymentGatewayProvider stub — only ParseWebhookAsync and ProviderName matter for replay.</summary>
    private sealed class TestProvider : IPaymentGatewayProvider
    {
        public string ProviderName { get; }
        public ProviderCapabilities Capabilities => ProviderCapabilities.Webhook;
        public Func<string, WebhookEvent?> Parse { get; }

        public TestProvider(string name, Func<string, WebhookEvent?>? parse = null)
        {
            ProviderName = name;
            Parse = parse ?? (p => new WebhookEvent { GatewayReference = p, Status = PaymentStatus.Completed });
        }

        public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public bool VerifyWebhookSignature(string payload, string signature) => true;

        public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) =>
            Task.FromResult(Parse(payload));
    }

    [Fact]
    public async Task TryRecord_DeduplicatesSameEventId()
    {
        var store = new InMemoryWebhookReplayStore();
        var first = await store.TryRecordAsync(Env("evt_1"));
        var dup = await store.TryRecordAsync(Env("evt_1"));

        Assert.True(first);
        Assert.False(dup);
    }

    [Fact]
    public async Task ListPending_OrdersByReceivedAtAscending()
    {
        var store = new InMemoryWebhookReplayStore();
        await store.TryRecordAsync(Env("evt_newest", secondsAgo: 1));
        await store.TryRecordAsync(Env("evt_oldest", secondsAgo: 100));
        await store.TryRecordAsync(Env("evt_middle", secondsAgo: 50));

        var pending = await store.ListPendingAsync("stripe");

        Assert.Equal(3, pending.Count);
        Assert.Equal("evt_oldest", pending[0].EventId);
        Assert.Equal("evt_middle", pending[1].EventId);
        Assert.Equal("evt_newest", pending[2].EventId);
    }

    [Fact]
    public async Task MarkHandled_RemovesFromPendingList()
    {
        var store = new InMemoryWebhookReplayStore();
        await store.TryRecordAsync(Env("evt_a"));
        await store.TryRecordAsync(Env("evt_b"));

        await store.MarkHandledAsync("evt_a");
        var pending = await store.ListPendingAsync("stripe");

        Assert.Single(pending);
        Assert.Equal("evt_b", pending[0].EventId);
    }

    [Fact]
    public async Task MarkFailed_IncrementsAttemptsAndCapturesReason()
    {
        var store = new InMemoryWebhookReplayStore();
        await store.TryRecordAsync(Env("evt_x"));
        await store.MarkFailedAsync("evt_x", "boom");
        await store.MarkFailedAsync("evt_x", "boom2");

        var pending = await store.ListPendingAsync("stripe");
        var item = Assert.Single(pending);
        Assert.Equal(WebhookHandlerStatus.Failed, item.Status);
        Assert.Equal(2, item.Attempts);
        Assert.Equal("boom2", item.FailureReason);
    }

    [Fact]
    public async Task ReplayAsync_ReturnsZero_WhenProviderUnknown()
    {
        var store = new InMemoryWebhookReplayStore();
        await store.TryRecordAsync(Env("evt_z", provider: "stripe"));
        var replayer = new WebhookReplayer(store, [], NullLogger<WebhookReplayer>.Instance);

        var count = await replayer.ReplayAsync("paystack", (_, _) => Task.CompletedTask);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ReplayAsync_CountsSuccessfulHandlerInvocations()
    {
        var store = new InMemoryWebhookReplayStore();
        await store.TryRecordAsync(Env("evt_1"));
        await store.TryRecordAsync(Env("evt_2"));
        await store.TryRecordAsync(Env("evt_3"));

        var provider = new TestProvider("stripe");
        var replayer = new WebhookReplayer(store, [provider], NullLogger<WebhookReplayer>.Instance);

        var handled = 0;
        var count = await replayer.ReplayAsync("stripe", (_, _) =>
        {
            Interlocked.Increment(ref handled);
            return Task.CompletedTask;
        });

        Assert.Equal(3, count);
        Assert.Equal(3, handled);

        // All envelopes should now be marked handled and therefore absent from ListPending.
        Assert.Empty(await store.ListPendingAsync("stripe"));
    }

    [Fact]
    public async Task ReplayAsync_MarksFailed_WhenHandlerThrows()
    {
        var store = new InMemoryWebhookReplayStore();
        // Use the EventId itself as the payload so the stub Parse function (payload → GatewayReference)
        // produces a GatewayReference we can route on.
        await store.TryRecordAsync(Env("evt_ok", payload: "evt_ok"));
        await store.TryRecordAsync(Env("evt_boom", payload: "evt_boom"));
        var provider = new TestProvider("stripe");
        var replayer = new WebhookReplayer(store, [provider], NullLogger<WebhookReplayer>.Instance);

        var count = await replayer.ReplayAsync("stripe", (evt, _) =>
            evt.GatewayReference.Contains("boom", StringComparison.Ordinal)
                ? throw new InvalidOperationException("handler exploded")
                : Task.CompletedTask);

        Assert.Equal(1, count);
        var pending = await store.ListPendingAsync("stripe");
        var failed = Assert.Single(pending);
        Assert.Equal(WebhookHandlerStatus.Failed, failed.Status);
        Assert.Contains("exploded", failed.FailureReason ?? string.Empty);
        Assert.Equal(1, failed.Attempts);
    }

    [Fact]
    public async Task ReplayAsync_MarksFailed_WhenParserReturnsNull()
    {
        var store = new InMemoryWebhookReplayStore();
        await store.TryRecordAsync(Env("evt_unknown"));
        var provider = new TestProvider("stripe", _ => null);
        var replayer = new WebhookReplayer(store, [provider], NullLogger<WebhookReplayer>.Instance);

        var count = await replayer.ReplayAsync("stripe", (_, _) => Task.CompletedTask);

        Assert.Equal(0, count);
        var pending = await store.ListPendingAsync("stripe");
        var failed = Assert.Single(pending);
        Assert.Equal(WebhookHandlerStatus.Failed, failed.Status);
        Assert.Contains("ParseWebhookAsync", failed.FailureReason ?? string.Empty);
    }
}
