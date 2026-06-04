// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Runtime.CompilerServices;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe Connect implementation of <see cref="IMarketplaceProvider"/>. Sub-accounts are Stripe
/// Express / Custom <c>Account</c> objects; split definitions are stored in the distributed cache
/// and applied as <c>Transfer</c> calls after the platform charge settles (Stripe's
/// "separate charges and transfers" pattern). The single-destination case is optimised into
/// a single PaymentIntent with <c>transfer_data.destination</c>.
/// </summary>
/// <remarks>
/// Stripe does NOT expose a server-side reusable "split" resource the way Paystack or
/// Flutterwave do — splits are computed per-charge. To keep the contract uniform we persist
/// split definitions via <see cref="IBhenguDistributedCache"/>; consumers running multiple
/// instances should register a Redis-backed cache so split references survive process restarts
/// and are visible across replicas.
/// </remarks>
public sealed class StripeMarketplaceProvider : BhenguProviderBase, IMarketplaceProvider
{
    private const string SplitKeyPrefix = "stripe:split:";
    private static readonly TimeSpan SplitTtl = TimeSpan.FromDays(365);

    private readonly StripeOptions _options;
    private readonly IStripeClient _stripeClient;
    private readonly IBhenguDistributedCache _cache;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeMarketplaceProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeMarketplaceProvider> logger,
        IBhenguDistributedCache cache)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StripeOptions.SecretKey)} is required");

        StripeConfiguration.ApiKey = _options.SecretKey;
        _stripeClient = new StripeClient(
            apiKey: _options.SecretKey,
            httpClient: new SystemNetHttpClient(httpClient));
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public StripeMarketplaceProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeMarketplaceProvider> logger)
        : this(httpClient, options, logger, new InMemoryBhenguDistributedCache())
    {
    }

    /// <inheritdoc />
    public Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_sub_account", async () =>
        {
            try
            {
                var requestOptions = BuildRequestOptions(request.IdempotencyKey);
                var accountService = new AccountService(_stripeClient);
                var createOptions = new AccountCreateOptions
                {
                    Type = "express",
                    Country = request.Country,
                    Email = request.ContactEmail,
                    DefaultCurrency = request.SettlementCurrency?.ToLowerInvariant(),
                    BusinessProfile = new AccountBusinessProfileOptions { Name = request.BusinessName },
                    Capabilities = new AccountCapabilitiesOptions
                    {
                        CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true },
                        Transfers = new AccountCapabilitiesTransfersOptions { Requested = true }
                    },
                    Metadata = request.Metadata?.ToDictionary(k => k.Key, v => v.Value)
                };

                var account = await accountService.CreateAsync(createOptions, requestOptions, ct).ConfigureAwait(false);
                Logger.LogInformation("Stripe Connect account created: {AccountId} country={Country}", account.Id, request.Country);

                // Generate an onboarding link if a return URL was supplied — required for Express accounts
                // before they can transact.
                string? onboardingUrl = null;
                if (!string.IsNullOrEmpty(request.ReturnUrl))
                {
                    ct.ThrowIfCancellationRequested();
                    var linkService = new AccountLinkService(_stripeClient);
                    var link = await linkService.CreateAsync(new AccountLinkCreateOptions
                    {
                        Account = account.Id,
                        RefreshUrl = request.ReturnUrl,
                        ReturnUrl = request.ReturnUrl,
                        Type = "account_onboarding"
                    }, cancellationToken: ct).ConfigureAwait(false);
                    onboardingUrl = link.Url;
                }

                return Map(account, request, onboardingUrl);
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "CreateSubAccount", Logger);
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subAccountReference);
        return RunOperationAsync("get_sub_account", async () =>
        {
            try
            {
                var service = new AccountService(_stripeClient);
                var account = await service.GetAsync(subAccountReference, cancellationToken: ct).ConfigureAwait(false);
                return (SubAccount?)Map(account);
            }
            catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "GetSubAccount", Logger);
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SubAccount> ListSubAccountsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var service = new AccountService(_stripeClient);
        var paginator = service.ListAutoPagingAsync(new AccountListOptions { Limit = 100 }, cancellationToken: ct);
        await foreach (var a in paginator.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            yield return Map(a);
        }
    }

    /// <inheritdoc />
    public Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_split", async () =>
        {
            if (request.Rules is null || request.Rules.Count == 0)
                throw new BhenguPaymentException(ProviderName, "SplitDefinitionRequest.Rules must contain at least one beneficiary");

            // Stripe has no first-class split resource. Persist in the distributed cache (365d TTL)
            // and surface a stable reference the caller can pass on subsequent ChargeWithSplit calls.
            var reference = $"split_{Guid.NewGuid():N}";
            var split = new SplitDefinition
            {
                Reference = reference,
                Name = request.Name,
                Currency = request.Currency,
                Rules = request.Rules
            };
            await _cache.SetAsync(SplitKeyPrefix + reference, split, SplitTtl, ct).ConfigureAwait(false);
            Logger.LogInformation("Stripe split definition cached: {SplitId} rules={RuleCount}", reference, request.Rules.Count);
            return split;
        }, ct);
    }

    /// <inheritdoc />
    public Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(splitReference);
        return RunOperationAsync("get_split", async () =>
            await _cache.GetAsync<SplitDefinition>(SplitKeyPrefix + splitReference, ct).ConfigureAwait(false), ct);
    }

    /// <inheritdoc />
    public Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payment);
        return RunChargeAsync(request.Payment.Currency, async () =>
        {
            var cachedRules = request.SplitReference is not null
                ? (await _cache.GetAsync<SplitDefinition>(SplitKeyPrefix + request.SplitReference, ct).ConfigureAwait(false))?.Rules
                : null;
            var rules = request.InlineRules ?? cachedRules;
            if (rules is null || rules.Count == 0)
                throw new BhenguPaymentException(ProviderName, "Either SplitReference (with rules registered) or InlineRules must be supplied");

            var requestOptions = BuildRequestOptions(request.Payment.IdempotencyKey);
            var amountInCents = (long)(request.Payment.Amount * 100);

            try
            {
                // Fast path: a single 100% rule maps to PaymentIntent.transfer_data.destination —
                // one HTTP call, no post-settle transfer juggling. Stripe Connect's canonical pattern.
                if (rules.Count == 1 && rules[0].ShareType == SplitShareType.Percentage && rules[0].Percentage is 100m)
                {
                    var single = rules[0];
                    var piService = new PaymentIntentService(_stripeClient);
                    var options = new PaymentIntentCreateOptions
                    {
                        Amount = amountInCents,
                        Currency = request.Payment.Currency.ToLowerInvariant(),
                        PaymentMethod = request.Payment.PaymentMethodToken,
                        Customer = request.Payment.CustomerId,
                        Description = request.Payment.Description,
                        Confirm = true,
                        ConfirmationMethod = "automatic",
                        TransferData = new PaymentIntentTransferDataOptions
                        {
                            Destination = single.SubAccountReference
                        },
                        Metadata = request.Payment.Metadata?.ToDictionary(k => k.Key, v => v.Value)
                    };
                    var intent = await piService.CreateAsync(options, requestOptions, ct).ConfigureAwait(false);
                    return new PaymentResponse
                    {
                        GatewayReference = intent.Id,
                        Status = MapPaymentStatus(intent.Status),
                        Amount = request.Payment.Amount,
                        Currency = request.Payment.Currency,
                        ProcessedAt = DateTime.UtcNow,
                        Message = intent.Status
                    };
                }

                // Multi-destination path: charge on the platform with a transfer_group, then issue
                // one Transfer per beneficiary post-settle. Stripe explicitly recommends this pattern
                // for marketplaces with N>1 splits per charge.
                var transferGroup = $"split_{Guid.NewGuid():N}";
                var intentMulti = await new PaymentIntentService(_stripeClient).CreateAsync(new PaymentIntentCreateOptions
                {
                    Amount = amountInCents,
                    Currency = request.Payment.Currency.ToLowerInvariant(),
                    PaymentMethod = request.Payment.PaymentMethodToken,
                    Customer = request.Payment.CustomerId,
                    Description = request.Payment.Description,
                    Confirm = true,
                    ConfirmationMethod = "automatic",
                    TransferGroup = transferGroup,
                    Metadata = request.Payment.Metadata?.ToDictionary(k => k.Key, v => v.Value)
                }, requestOptions, ct).ConfigureAwait(false);

                // Apply each split rule as a transfer. We only attempt this when the charge succeeded
                // synchronously; otherwise the consumer must drive transfers from the webhook (their
                // call — we surface the transfer_group on PaymentResponse.Message).
                if (intentMulti.Status?.Equals("succeeded", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var transferService = new TransferService(_stripeClient);
                    var gross = amountInCents;
                    foreach (var rule in rules)
                    {
                        ct.ThrowIfCancellationRequested();
                        var portionCents = rule.ShareType switch
                        {
                            SplitShareType.FixedAmount => (long)((rule.Amount ?? 0m) * 100),
                            SplitShareType.Percentage => (long)Math.Round(gross * ((rule.Percentage ?? 0m) / 100m)),
                            _ => 0L
                        };
                        if (portionCents <= 0) continue;

                        try
                        {
                            await transferService.CreateAsync(new TransferCreateOptions
                            {
                                Amount = portionCents,
                                Currency = request.Payment.Currency.ToLowerInvariant(),
                                Destination = rule.SubAccountReference,
                                TransferGroup = transferGroup,
                                SourceTransaction = intentMulti.LatestChargeId
                            }, cancellationToken: ct).ConfigureAwait(false);
                        }
                        catch (StripeException txEx)
                        {
                            Logger.LogError(txEx, "Stripe Transfer failed for sub-account {SubAccount} within {TransferGroup}",
                                rule.SubAccountReference, transferGroup);
                        }
                    }
                }

                return new PaymentResponse
                {
                    GatewayReference = intentMulti.Id,
                    Status = MapPaymentStatus(intentMulti.Status),
                    Amount = request.Payment.Amount,
                    Currency = request.Payment.Currency,
                    ProcessedAt = DateTime.UtcNow,
                    Message = $"transfer_group={transferGroup}"
                };
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "ChargeWithSplit", Logger);
            }
        }, ct);
    }

    private static SubAccount Map(Account account, SubAccountRequest? request = null, string? onboardingUrl = null) => new()
    {
        Reference = account.Id,
        BusinessName = account.BusinessProfile?.Name ?? request?.BusinessName ?? account.Id,
        ContactEmail = account.Email ?? request?.ContactEmail,
        SettlementAccountToken = null, // Stripe holds the external-account token internally; not surfaced on the Account itself.
        IsActive = account.ChargesEnabled && account.PayoutsEnabled,
        OnboardingUrl = onboardingUrl
    };

    private static PaymentStatus MapPaymentStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "succeeded" => PaymentStatus.Completed,
        "processing" => PaymentStatus.Pending,
        "requires_action" or "requires_confirmation" or "requires_payment_method" => PaymentStatus.Pending,
        "canceled" or "cancelled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    private static RequestOptions? BuildRequestOptions(string? idempotencyKey) =>
        string.IsNullOrEmpty(idempotencyKey) ? null : new RequestOptions { IdempotencyKey = idempotencyKey };
}
