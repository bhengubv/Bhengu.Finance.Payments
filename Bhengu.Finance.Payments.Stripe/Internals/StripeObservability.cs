// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Observability;
using Microsoft.Extensions.Logging;
using Stripe;

namespace Bhengu.Finance.Payments.Stripe.Internals;

/// <summary>
/// DRY OpenTelemetry observation helpers used by every public method on every Stripe provider.
/// Centralises the span / counter / histogram emission, exception classification (Declined /
/// RateLimited / Unavailable / Error) and re-throw semantics. Keeps each provider method body
/// free of repetitive try / catch boilerplate while keeping every counter and tag value identical
/// across providers — necessary for clean SLO dashboards.
/// </summary>
internal static class StripeObservability
{
    /// <summary>Observe a charge operation with span + counter + duration histogram.</summary>
    public static async Task<T> ObserveChargeAsync<T>(
        string currency,
        Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderNames.Stripe, currency);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        var start = Stopwatch.GetTimestamp();
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderNames.Stripe),
                new KeyValuePair<string, object?>("outcome", outcome));
            BhenguPaymentDiagnostics.ChargeDurationMs.Record(
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", ProviderNames.Stripe),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Observe a refund operation with span + counter.</summary>
    public static async Task<T> ObserveRefundAsync<T>(
        string gatewayReference,
        Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartRefundActivity(ProviderNames.Stripe, gatewayReference);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
            BhenguPaymentDiagnostics.RefundsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderNames.Stripe),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Observe a payout operation with span + counter.</summary>
    public static async Task<T> ObservePayoutAsync<T>(
        string currency,
        Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartPayoutActivity(ProviderNames.Stripe, currency);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
            BhenguPaymentDiagnostics.PayoutsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderNames.Stripe),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    /// <summary>Observe an arbitrary operation (Tokenise, CreateSubscription, etc.) with span only.</summary>
    public static async Task<T> ObserveAsync<T>(
        string operationName,
        Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderNames.Stripe, operationName);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (PaymentDeclinedException) { outcome = BhenguPaymentDiagnostics.Outcomes.Declined; throw; }
        catch (ProviderRateLimitException) { outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited; throw; }
        catch (ProviderUnavailableException) { outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable; throw; }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
        }
    }

    /// <summary>Observe a webhook parsing operation; emits the verification counter using the supplied flag.</summary>
    public static async Task<T> ObserveWebhookAsync<T>(Func<Task<T>> body)
    {
        using var activity = BhenguPaymentDiagnostics.StartWebhookActivity(ProviderNames.Stripe);
        var outcome = BhenguPaymentDiagnostics.Outcomes.Success;
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch { outcome = BhenguPaymentDiagnostics.Outcomes.Error; throw; }
        finally
        {
            activity.SetOutcome(outcome);
        }
    }

    /// <summary>Record a webhook signature verification result without an activity (sync caller path).</summary>
    public static void RecordWebhookVerification(bool verified)
    {
        BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
            new KeyValuePair<string, object?>("provider", ProviderNames.Stripe),
            new KeyValuePair<string, object?>("valid", verified.ToString()));
    }

    /// <summary>
    /// Translates Stripe SDK exceptions into Bhengu exception types so the outcome classifier
    /// in <see cref="ObserveChargeAsync{T}"/> et al. picks the correct outcome tag. Reused across
    /// every provider.
    /// </summary>
    public static BhenguPaymentException TranslateStripeException(
        StripeException ex,
        string operation,
        Microsoft.Extensions.Logging.ILogger logger)
    {
        var httpStatus = (int)ex.HttpStatusCode;
        var errorCode = ex.StripeError?.Code ?? ex.HttpStatusCode.ToString();
        var errorMessage = ex.StripeError?.Message ?? ex.Message;

        logger.LogError(ex, "Stripe {Operation} failed: {HttpStatus} {Code} {Message}",
            operation, httpStatus, errorCode, errorMessage);

        if (httpStatus == 429)
            return new ProviderRateLimitException(ProviderNames.Stripe, providerErrorMessage: errorMessage, innerException: ex);

        if (httpStatus is >= 400 and < 500)
            return new PaymentDeclinedException(ProviderNames.Stripe, errorCode, errorMessage, ex);

        return new ProviderUnavailableException(ProviderNames.Stripe, $"HTTP {httpStatus}: {errorMessage}", ex);
    }
}
