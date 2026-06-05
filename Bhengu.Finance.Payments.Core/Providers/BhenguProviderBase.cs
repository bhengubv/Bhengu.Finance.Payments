// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Diagnostics;
using Bhengu.Finance.Payments.Core.Auditing;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Observability;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Core.Providers;

/// <summary>
/// Shared base for every Bhengu.Finance.Payments provider — collapses the boilerplate every
/// provider would otherwise repeat: open an OpenTelemetry activity, increment the right counter,
/// record the duration histogram, translate exceptions into the canonical Bhengu hierarchy,
/// stamp an outcome tag, and re-throw the original exception.
///
/// <para>Providers call <see cref="RunChargeAsync{T}"/> / <see cref="RunRefundAsync{T}"/> /
/// <see cref="RunPayoutAsync{T}"/> / <see cref="RunOperationAsync{T}"/> /
/// <see cref="RunWebhookVerifyAsync"/> instead of writing their own try/catch + diagnostics
/// blocks. ~140 nearly-identical wrappers across the family collapse to four lines per method.</para>
///
/// <para>The wrappers also perform <see cref="CancellationToken.ThrowIfCancellationRequested"/>
/// before invoking the inner operation, ensuring late cancellations are honoured even when the
/// inner code path doesn't poll the token between sequential HTTP calls.</para>
/// </summary>
public abstract class BhenguProviderBase
{
    /// <summary>Provider-supplied logger. Pass to the base constructor.</summary>
    protected ILogger Logger { get; }

    /// <summary>Provider canonical name (use <see cref="ProviderNames"/>).</summary>
    public abstract string ProviderName { get; }

    /// <summary>Construct the base with the provider's logger.</summary>
    protected BhenguProviderBase(ILogger logger)
    {
        Logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Wrap a charge-like operation (ProcessPaymentAsync, ChargeWithSplitAsync, ChargeMandateAsync,
    /// SubscriptionRenewal). Records to ChargesTotal counter + ChargeDurationMs histogram + spans.
    /// </summary>
    protected Task<T> RunChargeAsync<T>(string currency, Func<Task<T>> op, CancellationToken ct)
        => RunAsync(
            BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, currency),
            BhenguPaymentDiagnostics.ChargesTotal,
            BhenguPaymentDiagnostics.ChargeDurationMs,
            op, ct);

    /// <summary>
    /// Wrap a refund operation. Records to RefundsTotal counter + spans. No duration histogram —
    /// refund latency profile is less interesting than charge latency for SLO dashboards.
    /// </summary>
    protected Task<T> RunRefundAsync<T>(string gatewayReference, Func<Task<T>> op, CancellationToken ct)
        => RunAsync(
            BhenguPaymentDiagnostics.StartRefundActivity(ProviderName, gatewayReference),
            BhenguPaymentDiagnostics.RefundsTotal,
            durationHistogram: null,
            op, ct);

    /// <summary>
    /// Wrap a payout operation. Records to PayoutsTotal counter + spans.
    /// </summary>
    protected Task<T> RunPayoutAsync<T>(string currency, Func<Task<T>> op, CancellationToken ct)
        => RunAsync(
            BhenguPaymentDiagnostics.StartPayoutActivity(ProviderName, currency),
            BhenguPaymentDiagnostics.PayoutsTotal,
            durationHistogram: null,
            op, ct);

    /// <summary>
    /// Wrap a generic operation — Tokenise, CreateSubscription, GenerateQr, CreateMandate,
    /// CreateSplit, GetSettlement, etc. Records to spans only (no per-operation counter).
    /// </summary>
    protected Task<T> RunOperationAsync<T>(string operationName, Func<Task<T>> op, CancellationToken ct)
        => RunAsync(
            BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, operationName),
            counter: null,
            durationHistogram: null,
            op, ct);

    /// <summary>
    /// Wrap webhook signature verification. Returns the verification result and increments the
    /// WebhookVerificationsTotal counter tagged with valid=true|false.
    /// </summary>
    protected bool RunWebhookVerify(Func<bool> verify)
    {
        ArgumentNullException.ThrowIfNull(verify);
        bool valid = false;
        try
        {
            valid = verify();
            return valid;
        }
        finally
        {
            BhenguPaymentDiagnostics.WebhookVerificationsTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("valid", valid.ToString().ToLowerInvariant()));
        }
    }

    private async Task<T> RunAsync<T>(
        Activity? activity,
        System.Diagnostics.Metrics.Counter<long>? counter,
        System.Diagnostics.Metrics.Histogram<double>? durationHistogram,
        Func<Task<T>> op,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(op);
        ct.ThrowIfCancellationRequested();

        var start = Stopwatch.GetTimestamp();
        string outcome = BhenguPaymentDiagnostics.Outcomes.Error;
        string? errorType = null;
        string? errorCode = null;
        try
        {
            var result = await op().ConfigureAwait(false);
            outcome = BhenguPaymentDiagnostics.Outcomes.Success;
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = BhenguPaymentDiagnostics.Outcomes.Error;
            errorType = nameof(OperationCanceledException);
            throw;
        }
        catch (PaymentDeclinedException ex)
        {
            outcome = BhenguPaymentDiagnostics.Outcomes.Declined;
            errorType = nameof(PaymentDeclinedException);
            errorCode = ex.ProviderErrorCode;
            throw;
        }
        catch (ProviderRateLimitException ex)
        {
            outcome = BhenguPaymentDiagnostics.Outcomes.RateLimited;
            errorType = nameof(ProviderRateLimitException);
            errorCode = ex.ProviderErrorCode;
            throw;
        }
        catch (ProviderUnavailableException ex)
        {
            outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable;
            errorType = nameof(ProviderUnavailableException);
            errorCode = ex.ProviderErrorCode;
            throw;
        }
        catch (HttpRequestException ex)
        {
            // Translate raw HTTP failures into the canonical hierarchy so providers don't each
            // re-implement the same translation.
            outcome = BhenguPaymentDiagnostics.Outcomes.Unavailable;
            errorType = nameof(ProviderUnavailableException);
            throw new ProviderUnavailableException(ProviderName, "HTTP request failed", ex);
        }
        catch (BhenguPaymentException ex)
        {
            outcome = BhenguPaymentDiagnostics.Outcomes.Error;
            errorType = ex.GetType().Name;
            errorCode = ex.ProviderErrorCode;
            throw;
        }
        catch (Exception ex)
        {
            outcome = BhenguPaymentDiagnostics.Outcomes.Error;
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            activity.SetOutcome(outcome);
            activity?.Dispose();
            counter?.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", outcome));
            if (durationHistogram is not null)
            {
                durationHistogram.Record(elapsedMs,
                    new KeyValuePair<string, object?>("provider", ProviderName),
                    new KeyValuePair<string, object?>("outcome", outcome));
            }

            // Fire-and-forget audit emission via the process-wide ambient sink. Defaults to
            // NoopAuditLog when AddBhenguPaymentAuditLog hasn't been wired — never throws.
            try
            {
                var entry = new PaymentAuditEntry
                {
                    At = DateTime.UtcNow,
                    Provider = ProviderName,
                    Operation = activity?.OperationName ?? "unknown",
                    Outcome = outcome,
                    DurationMs = elapsedMs,
                    ErrorType = errorType,
                    ErrorCode = errorCode,
                };
                _ = BhenguPaymentAuditing.Default.RecordAsync(entry, CancellationToken.None);
            }
            catch
            {
                // Audit failure must never affect the original operation outcome.
            }
        }
    }
}
