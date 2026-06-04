// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Settlement;
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
public sealed class StripeSettlementProvider : ISettlementProvider
{
    private readonly StripeOptions _options;
    private readonly ILogger<StripeSettlementProvider> _logger;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeSettlementProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeSettlementProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StripeOptions.SecretKey)} is required");

        StripeConfiguration.ApiKey = _options.SecretKey;
        _stripeClient = new StripeClient(
            apiKey: _options.SecretKey,
            httpClient: new SystemNetHttpClient(httpClient));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Settlement>> ListSettlementsAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => StripeObservability.ObserveAsync("list_settlements", () => ListSettlementsCoreAsync(fromUtc, toUtc, ct));

    private async Task<IReadOnlyList<Settlement>> ListSettlementsCoreAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        try
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
            var page = await service.ListAsync(listOptions, cancellationToken: ct).ConfigureAwait(false);
            return page.Data.Select(Map).ToList();
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ListSettlements");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public Task<Settlement?> GetSettlementAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return StripeObservability.ObserveAsync("get_settlement", () => GetSettlementCoreAsync(settlementReference, ct));
    }

    private async Task<Settlement?> GetSettlementCoreAsync(string settlementReference, CancellationToken ct)
    {
        try
        {
            var service = new PayoutService(_stripeClient);
            var payout = await service.GetAsync(settlementReference, cancellationToken: ct).ConfigureAwait(false);
            return Map(payout);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "GetSettlement");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SettlementTransaction>> ListTransactionsAsync(string settlementReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(settlementReference);
        return StripeObservability.ObserveAsync("list_settlement_transactions", () => ListTransactionsCoreAsync(settlementReference, ct));
    }

    private async Task<IReadOnlyList<SettlementTransaction>> ListTransactionsCoreAsync(string settlementReference, CancellationToken ct)
    {
        try
        {
            var service = new BalanceTransactionService(_stripeClient);
            var listOptions = new BalanceTransactionListOptions
            {
                Payout = settlementReference,
                Limit = 100
            };
            var page = await service.ListAsync(listOptions, cancellationToken: ct).ConfigureAwait(false);
            return page.Data.Select(MapTx).ToList();
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ListSettlementTransactions");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
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

    private BhenguPaymentException TranslateException(StripeException ex, string operation)
    {
        var httpStatus = (int)ex.HttpStatusCode;
        var errorCode = ex.StripeError?.Code ?? ex.HttpStatusCode.ToString();
        var errorMessage = ex.StripeError?.Message ?? ex.Message;

        _logger.LogError(ex, "Stripe {Operation} failed: {HttpStatus} {Code} {Message}",
            operation, httpStatus, errorCode, errorMessage);

        if (httpStatus == 429)
            return new ProviderRateLimitException(ProviderName, providerErrorMessage: errorMessage, innerException: ex);

        if (httpStatus is >= 400 and < 500)
            return new PaymentDeclinedException(ProviderName, errorCode, errorMessage, ex);

        return new ProviderUnavailableException(ProviderName, $"HTTP {httpStatus}: {errorMessage}", ex);
    }
}
