// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Runtime.CompilerServices;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Settlement = Bhengu.Finance.Payments.Core.Models.Settlement.Settlement;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe implementation of <see cref="ISettlementProvider"/>. On Stripe a "settlement" is a
/// <c>Payout</c> from the platform balance, and its constituent line items are
/// <c>BalanceTransaction</c> records joined back to that payout. This provider folds the two
/// resources into the unified <see cref="Settlement"/> contract.
/// </summary>
public sealed class StripeSettlementProvider : BhenguProviderBase, ISettlementProvider
{
    private readonly StripeOptions _options;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeSettlementProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeSettlementProvider> logger)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StripeOptions.SecretKey)} is required");

        StripeConfiguration.ApiKey = _options.SecretKey;
        _stripeClient = new StripeClient(
            apiKey: _options.SecretKey,
            httpClient: new SystemNetHttpClient(httpClient));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Settlement> ListSettlementsAsync(
        DateTime fromUtc,
        DateTime toUtc,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var service = new PayoutService(_stripeClient);
        var listOptions = new PayoutListOptions
        {
            Limit = 100,
            ArrivalDate = new DateRangeOptions
            {
                GreaterThanOrEqual = fromUtc,
                LessThanOrEqual = toUtc
            }
        };

        // Manual enumerator so we can translate StripeException → canonical hierarchy
        // around MoveNextAsync. `try`/`catch` cannot wrap `yield return` inside an iterator.
        var enumerator = service.ListAutoPagingAsync(listOptions, cancellationToken: ct)
            .GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                Payout? current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) yield break;
                    current = enumerator.Current;
                }
                catch (StripeException ex)
                {
                    throw StripeExceptionTranslator.Translate(ex, ProviderName, "ListSettlements", Logger);
                }
                catch (HttpRequestException ex)
                {
                    throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
                }
                ct.ThrowIfCancellationRequested();
                yield return Map(current);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return RunOperationAsync("get_settlement", async () =>
        {
            try
            {
                var service = new PayoutService(_stripeClient);
                var payout = await service.GetAsync(settlementReference, cancellationToken: ct).ConfigureAwait(false);
                return (Settlement?)Map(payout);
            }
            catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "GetSettlement", Logger);
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SettlementTransaction> ListTransactionsAsync(
        string settlementReference,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);

        var service = new BalanceTransactionService(_stripeClient);
        var listOptions = new BalanceTransactionListOptions
        {
            Payout = settlementReference,
            Limit = 100
        };

        var enumerator = service.ListAutoPagingAsync(listOptions, cancellationToken: ct)
            .GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                BalanceTransaction? current;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) yield break;
                    current = enumerator.Current;
                }
                catch (StripeException ex)
                {
                    throw StripeExceptionTranslator.Translate(ex, ProviderName, "ListTransactions", Logger);
                }
                catch (HttpRequestException ex)
                {
                    throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
                }
                ct.ThrowIfCancellationRequested();
                yield return MapTx(current);
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Settlement Map(Payout p) => new()
    {
        Reference = p.Id,
        NetAmount = p.Amount / 100m,
        // Stripe doesn't expose gross/fees on the Payout itself — those live on the constituent
        // BalanceTransactions (per-line) and on the payout BalanceTransaction (cumulative).
        GrossAmount = null,
        Fees = null,
        Currency = (p.Currency ?? "usd").ToUpperInvariant(),
        SettledAt = p.ArrivalDate,
        BankAccountReference = p.DestinationId,
        TransactionCount = 0
    };

    private static SettlementTransaction MapTx(BalanceTransaction bt) => new()
    {
        GatewayReference = bt.SourceId ?? bt.Id,
        Kind = MapKind(bt.Type),
        NetAmount = bt.Net / 100m,
        GrossAmount = bt.Amount / 100m,
        Fee = bt.Fee / 100m,
        Currency = (bt.Currency ?? "usd").ToUpperInvariant(),
        CreatedAt = bt.Created
    };

    private static SettlementTransactionKind MapKind(string? type) => type?.ToLowerInvariant() switch
    {
        "charge" or "payment" => SettlementTransactionKind.Charge,
        "refund" or "payment_refund" or "payment_failure_refund" => SettlementTransactionKind.Refund,
        "adjustment" => SettlementTransactionKind.Adjustment,
        "stripe_fee" or "application_fee" or "stripe_fx_fee" or "tax_fee" => SettlementTransactionKind.Fee,
        "payout_failure" or "transfer_failure" => SettlementTransactionKind.Adjustment,
        _ when (type?.Contains("dispute", StringComparison.OrdinalIgnoreCase) ?? false) => SettlementTransactionKind.Chargeback,
        _ => SettlementTransactionKind.Other
    };
}
