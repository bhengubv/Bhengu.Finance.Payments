// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Bhengu.Finance.Payments.Core.Observability;

/// <summary>
/// Diagnostic primitives for OpenTelemetry-compatible observability across all providers in the
/// Bhengu.Finance.Payments family. Consumers wire these up via:
///
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(b => b.AddSource(BhenguPaymentDiagnostics.ActivitySourceName))
///     .WithMetrics(b => b.AddMeter(BhenguPaymentDiagnostics.MeterName));
/// </code>
///
/// Provider implementations call <see cref="StartChargeActivity"/> / <see cref="StartRefundActivity"/>
/// etc. to emit spans. Counters and histograms record outcome volumes and latency for SLO dashboards.
/// </summary>
public static class BhenguPaymentDiagnostics
{
    /// <summary>OpenTelemetry tracing source name. Add to your tracer provider.</summary>
    public const string ActivitySourceName = "Bhengu.Finance.Payments";

    /// <summary>OpenTelemetry metrics meter name. Add to your meter provider.</summary>
    public const string MeterName = "Bhengu.Finance.Payments";

    /// <summary>SDK version reported on every span and metric, for slicing by SDK release.</summary>
    public static readonly string SdkVersion = typeof(BhenguPaymentDiagnostics).Assembly
        .GetName().Version?.ToString() ?? "unknown";

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName, SdkVersion);
    private static readonly Meter s_meter = new(MeterName, SdkVersion);

    /// <summary>Total charges attempted. Tag: <c>provider</c>, <c>outcome</c> (success|declined|error).</summary>
    public static readonly Counter<long> ChargesTotal =
        s_meter.CreateCounter<long>("bhengu_payments_charges_total", unit: "{charge}",
            description: "Number of payment charges attempted, tagged by provider and outcome.");

    /// <summary>Total refunds attempted. Tag: <c>provider</c>, <c>outcome</c>.</summary>
    public static readonly Counter<long> RefundsTotal =
        s_meter.CreateCounter<long>("bhengu_payments_refunds_total", unit: "{refund}",
            description: "Number of refunds attempted, tagged by provider and outcome.");

    /// <summary>Total payouts attempted. Tag: <c>provider</c>, <c>outcome</c>.</summary>
    public static readonly Counter<long> PayoutsTotal =
        s_meter.CreateCounter<long>("bhengu_payments_payouts_total", unit: "{payout}",
            description: "Number of payouts attempted, tagged by provider and outcome.");

    /// <summary>Charge duration in milliseconds. Tag: <c>provider</c>, <c>outcome</c>.</summary>
    public static readonly Histogram<double> ChargeDurationMs =
        s_meter.CreateHistogram<double>("bhengu_payments_charge_duration_ms", unit: "ms",
            description: "End-to-end duration of ProcessPaymentAsync, tagged by provider and outcome.");

    /// <summary>Webhook verification attempts. Tag: <c>provider</c>, <c>valid</c> (true|false).</summary>
    public static readonly Counter<long> WebhookVerificationsTotal =
        s_meter.CreateCounter<long>("bhengu_payments_webhook_verifications_total", unit: "{verification}",
            description: "Webhook signature verifications, tagged by provider and validity.");

    /// <summary>Start an Activity span for ProcessPaymentAsync. Returns null if no listener.</summary>
    public static Activity? StartChargeActivity(string providerName, string currency)
    {
        var activity = s_activitySource.StartActivity("payment.charge", ActivityKind.Client);
        activity?.SetTag("payment.provider", providerName);
        activity?.SetTag("payment.currency", currency);
        return activity;
    }

    /// <summary>Start an Activity span for ProcessRefundAsync.</summary>
    public static Activity? StartRefundActivity(string providerName, string gatewayReference)
    {
        var activity = s_activitySource.StartActivity("payment.refund", ActivityKind.Client);
        activity?.SetTag("payment.provider", providerName);
        activity?.SetTag("payment.gateway_reference", gatewayReference);
        return activity;
    }

    /// <summary>Start an Activity span for ProcessPayoutAsync.</summary>
    public static Activity? StartPayoutActivity(string providerName, string currency)
    {
        var activity = s_activitySource.StartActivity("payment.payout", ActivityKind.Client);
        activity?.SetTag("payment.provider", providerName);
        activity?.SetTag("payment.currency", currency);
        return activity;
    }

    /// <summary>
    /// Standard outcome tag values. Use these to keep dashboards consistent across providers.
    /// </summary>
    public static class Outcomes
    {
        public const string Success = "success";
        public const string Pending = "pending";
        public const string Declined = "declined";
        public const string RateLimited = "rate_limited";
        public const string Unavailable = "unavailable";
        public const string Error = "error";
    }
}
