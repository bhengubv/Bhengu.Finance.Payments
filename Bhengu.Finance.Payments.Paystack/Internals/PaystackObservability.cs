// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Observability;

namespace Bhengu.Finance.Payments.Paystack.Internals;

/// <summary>
/// DRY OpenTelemetry observation helpers used by every public method on every Paystack provider.
/// Centralises the span / counter / histogram emission, exception classification (Declined /
/// RateLimited / Unavailable / Error) and re-throw semantics. Keeps each provider method body
/// free of repetitive try / catch boilerplate.
/// </summary>
internal static class PaystackObservability
{
    /// <summary>Observe a charge operation with span + counter + duration histogram.</summary>
    public static async Task<T> ObserveChargeAsync<T>(string currency, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderNames.Paystack, currency);
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
                new KeyValuePair<string, object?>("provider", ProviderNames.Paystack),
                new KeyValuePair<string, object?>("outcome", outcome));
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderNames.Paystack),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Observe a refund operation with span + counter.</summary>
    public static async Task<T> ObserveRefundAsync<T>(string gatewayReference, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderNames.Paystack, gatewayReference);
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
                new KeyValuePair<string, object?>("provider", ProviderNames.Paystack),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Observe a payout operation with span + counter.</summary>
    public static async Task<T> ObservePayoutAsync<T>(string currency, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderNames.Paystack, currency);
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
                new KeyValuePair<string, object?>("provider", ProviderNames.Paystack),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Observe an arbitrary operation (Tokenise, CreateSubscription, etc.) with span only.</summary>
    public static async Task<T> ObserveAsync<T>(string operationName, Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderNames.Paystack, operationName);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try { return await body().ConfigureAwait(false); }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally { activity.SetOutcome(outcome); }
    }

    /// <summary>Observe a webhook parsing operation.</summary>
    public static async Task<T> ObserveWebhookAsync<T>(Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderNames.Paystack);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try { return await body().ConfigureAwait(false); }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally { activity.SetOutcome(outcome); }
    }

    /// <summary>Record a webhook signature verification result.</summary>
    public static void RecordWebhookVerification(bool verified)
    {
        BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderNames.Paystack),
            new KeyValuePair<string, object?>("valid", verified.ToString()));
    }
}
