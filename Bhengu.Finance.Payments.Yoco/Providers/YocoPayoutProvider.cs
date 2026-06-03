// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net.Http.Headers;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco implementation of <see cref="IPayoutProvider"/>.
/// </summary>
/// <remarks>
/// <para>Yoco's documented merchant API does NOT expose an on-demand payout endpoint. Payouts are
/// scheduled and initiated by the acquirer (Yoco) on a fixed cadence — typically T+1 weekday — and
/// the merchant is informed via the <c>payout.completed</c> / <c>payout.failed</c> webhooks.</para>
/// <para><see cref="ProcessPayoutAsync"/> therefore throws a <see cref="BhenguPaymentException"/>
/// directing callers to <see cref="ISettlementProvider"/>'s <c>ListSettlementsAsync</c> for
/// reading historical payouts, and to the webhook stream for receiving the lifecycle events.</para>
/// </remarks>
public sealed class YocoPayoutProvider : IPayoutProvider
{
    private readonly YocoOptions _options;
    private readonly ILogger<YocoPayoutProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Yoco;

    /// <summary>Construct a Yoco payout provider. Designed to be registered via DI.</summary>
    public YocoPayoutProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoPayoutProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (httpClient.BaseAddress is null)
            httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        if (httpClient.DefaultRequestHeaders.Authorization is null)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc/>
    /// <exception cref="BhenguPaymentException">Always — Yoco does not support on-demand payouts.</exception>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        _logger.LogWarning(
            "Yoco payout requested for amount={Amount} {Currency} destination={Destination}, but Yoco does not support on-demand payouts. " +
            "Use ISettlementProvider.ListSettlementsAsync to query scheduled payouts.",
            request.Amount, request.Currency, request.DestinationToken);

        throw new BhenguPaymentException(
            ProviderName,
            "Yoco payouts are scheduled by the acquirer; use the dashboard or webhook to monitor them. " +
            "ListPayoutsAsync is available via ISettlementProvider.",
            providerErrorCode: "payout_not_supported",
            providerErrorMessage: "Yoco does not expose an on-demand payout API.");
    }
}
