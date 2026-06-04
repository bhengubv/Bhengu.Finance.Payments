// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Observability;

namespace Bhengu.Finance.Payments.Razorpay.Internals;

/// <summary>DRY OpenTelemetry observation helpers for the Razorpay provider family.</summary>
internal static class RazorpayObservability
{
    public static async Task<T> ObserveChargeAsync<T>(string currency, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderNames.Razorpay, currency);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        var start = Stopwatch.GetTimestamp();
        try { return await body().ConfigureAwait(false); }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderNames.Razorpay),
                new KeyValuePair<string, object?>("outcome", outcome));
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderNames.Razorpay),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public static async Task<T> ObserveRefundAsync<T>(string gatewayReference, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderNames.Razorpay, gatewayReference);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try { return await body().ConfigureAwait(false); }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
            BhenguPaymentDiagnostics.RefundsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderNames.Razorpay),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public static async Task<T> ObservePayoutAsync<T>(string currency, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderNames.Razorpay, currency);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try { return await body().ConfigureAwait(false); }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderNames.Razorpay),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public static async Task<T> ObserveAsync<T>(string operationName, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderNames.Razorpay, operationName);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try { return await body().ConfigureAwait(false); }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally { activity.SetOutcome(outcome); }
    }

    public static async Task<T> ObserveWebhookAsync<T>(Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderNames.Razorpay);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try { return await body().ConfigureAwait(false); }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally { activity.SetOutcome(outcome); }
    }

    public static void RecordWebhookVerification(bool verified)
    {
        BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderNames.Razorpay),
            new KeyValuePair<string, object?>("valid", verified.ToString()));
    }
}
