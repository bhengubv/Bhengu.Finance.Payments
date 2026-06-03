// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe Connect implementation of <see cref="IMarketplaceProvider"/>. Sub-accounts are Stripe
/// Express / Custom <c>Account</c> objects; split definitions are stored in process memory and
/// applied as <c>Transfer</c> calls after the platform charge settles (Stripe's
/// "separate charges and transfers" pattern). The single-destination case is optimised into
/// a single PaymentIntent with <c>transfer_data.destination</c>.
/// </summary>
/// <remarks>
/// Stripe does NOT expose a server-side reusable "split" resource the way Paystack or
/// Flutterwave do — splits are computed per-charge. To keep the contract uniform we persist
/// split definitions in process memory; consumers that need multi-instance / durable splits
/// should serialise them in their own store and re-create the <see cref="StripeMarketplaceProvider"/>
/// with the rules on hand.
/// </remarks>
public sealed class StripeMarketplaceProvider : IMarketplaceProvider
{
    private readonly StripeOptions _options;
    private readonly ILogger<StripeMarketplaceProvider> _logger;
    private readonly IStripeClient _stripeClient;
    private readonly ConcurrentDictionary<string, SplitDefinition> _splits = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeMarketplaceProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeMarketplaceProvider> logger)
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
    public async Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestOptions = BuildRequestOptions(request.IdempotencyKey);

        try
        {
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
            _logger.LogInformation("Stripe Connect account created: {AccountId} country={Country}", account.Id, request.Country);

            // Generate an onboarding link if a return URL was supplied — required for Express accounts
            // before they can transact.
            string? onboardingUrl = null;
            if (!string.IsNullOrEmpty(request.ReturnUrl))
            {
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
            throw TranslateException(ex, "CreateSubAccount");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(subAccountReference);

        try
        {
            var service = new AccountService(_stripeClient);
            var account = await service.GetAsync(subAccountReference, cancellationToken: ct).ConfigureAwait(false);
            return Map(account);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "GetSubAccount");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SubAccount>> ListSubAccountsAsync(CancellationToken ct = default)
    {
        try
        {
            var service = new AccountService(_stripeClient);
            var page = await service.ListAsync(new AccountListOptions { Limit = 100 }, cancellationToken: ct).ConfigureAwait(false);
            return page.Data.Select(a => Map(a)).ToList();
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ListSubAccounts");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Rules is null || request.Rules.Count == 0)
            throw new BhenguPaymentException(ProviderName, "SplitDefinitionRequest.Rules must contain at least one beneficiary");

        // Stripe has no first-class split resource. Persist locally and surface a stable reference
        // the caller can pass on subsequent ChargeWithSplit calls.
        var reference = $"split_{Guid.NewGuid():N}";
        var split = new SplitDefinition
        {
            Reference = reference,
            Name = request.Name,
            Currency = request.Currency,
            Rules = request.Rules
        };
        _splits[reference] = split;
        _logger.LogInformation("Stripe in-memory split definition created: {SplitId} rules={RuleCount}", reference, request.Rules.Count);
        return Task.FromResult(split);
    }

    /// <inheritdoc />
    public Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(splitReference);
        _splits.TryGetValue(splitReference, out var split);
        return Task.FromResult(split);
    }

    /// <inheritdoc />
    public async Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Payment);

        var rules = request.InlineRules ?? (request.SplitReference is not null && _splits.TryGetValue(request.SplitReference, out var def) ? def.Rules : null);
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
                        _logger.LogError(txEx, "Stripe Transfer failed for sub-account {SubAccount} within {TransferGroup}",
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
            throw TranslateException(ex, "ChargeWithSplit");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
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
