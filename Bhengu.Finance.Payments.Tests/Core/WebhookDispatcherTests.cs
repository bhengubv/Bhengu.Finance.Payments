// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Webhooks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bhengu.Finance.Payments.Tests.CoreFoundations;

/// <summary>
/// Verifies <see cref="WebhookEventDispatcher"/> end-to-end: signature check, parse, dedup, handler invoke.
/// </summary>
public class WebhookDispatcherTests
{
    private sealed class StubProvider(string name, bool validSig, WebhookEvent? parse) : IPaymentGatewayProvider
    {
        public string ProviderName => name;
        public ProviderCapabilities Capabilities => ProviderCapabilities.Webhook;
        public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RefundResponse> ProcessRefundAsync(RefundRequest r, CancellationToken ct = default) => throw new NotSupportedException();
        public bool VerifyWebhookSignature(string payload, string signature) => validSig;
        public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) => Task.FromResult(parse);
    }

    private static WebhookEventDispatcher Build(IPaymentGatewayProvider provider, out InMemoryWebhookReplayStore store)
    {
        store = new InMemoryWebhookReplayStore();
        return new WebhookEventDispatcher([provider], store, NullLogger<WebhookEventDispatcher>.Instance);
    }

    [Fact]
    public async Task UnknownProviderReturnsUnknownProvider()
    {
        var d = Build(new StubProvider("stripe", true, null), out _);
        var outcome = await d.DispatchAsync("paystack", "{}", "sig", (_, _) => Task.CompletedTask);
        Assert.Equal(WebhookDispatchOutcome.UnknownProvider, outcome);
    }

    [Fact]
    public async Task InvalidSignatureReturnsInvalidSignature()
    {
        var d = Build(new StubProvider("stripe", false, null), out _);
        var outcome = await d.DispatchAsync("stripe", "{}", "bad", (_, _) => Task.CompletedTask);
        Assert.Equal(WebhookDispatchOutcome.InvalidSignature, outcome);
    }

    [Fact]
    public async Task UnknownEventReturnsUnknownEvent()
    {
        var d = Build(new StubProvider("stripe", true, null), out _);
        var outcome = await d.DispatchAsync("stripe", "{}", "sig", (_, _) => Task.CompletedTask);
        Assert.Equal(WebhookDispatchOutcome.UnknownEvent, outcome);
    }

    [Fact]
    public async Task HandlerInvokedExactlyOncePerEventId()
    {
        var evt = new WebhookEvent { GatewayReference = "evt_1", Status = PaymentStatus.Completed, Category = WebhookEventCategory.ChargeSucceeded };
        var d = Build(new StubProvider("stripe", true, evt), out var store);

        var invocations = 0;
        var outcome1 = await d.DispatchAsync("stripe", "{}", "sig", (_, _) => { Interlocked.Increment(ref invocations); return Task.CompletedTask; });
        var outcome2 = await d.DispatchAsync("stripe", "{}", "sig", (_, _) => { Interlocked.Increment(ref invocations); return Task.CompletedTask; });

        Assert.Equal(WebhookDispatchOutcome.Handled, outcome1);
        Assert.Equal(WebhookDispatchOutcome.Duplicate, outcome2);
        Assert.Equal(1, invocations);
    }

    [Fact]
    public async Task HandlerExceptionRecordsFailureAndReturnsHandlerFailed()
    {
        var evt = new WebhookEvent { GatewayReference = "evt_fail", Status = PaymentStatus.Failed, Category = WebhookEventCategory.ChargeFailed };
        var d = Build(new StubProvider("stripe", true, evt), out var store);

        var outcome = await d.DispatchAsync("stripe", "{}", "sig", (_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(WebhookDispatchOutcome.HandlerFailed, outcome);
        var pending = await store.ListPendingAsync("stripe");
        var failed = Assert.Single(pending);
        Assert.Equal(WebhookHandlerStatus.Failed, failed.Status);
        Assert.Contains("boom", failed.FailureReason ?? "");
    }
}
