// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay Route marketplace provider. Wraps the Linked Accounts (<c>/v2/accounts</c>) and
/// Transfers (<c>/v1/transfers</c>) endpoints to onboard sub-merchants and split charges atomically.
/// </summary>
/// <remarks>
/// Razorpay Route's atomic-split flow uses the <c>transfers</c> parameter on order create OR a
/// follow-up POST against an already-captured payment. We use the latter for the SDK's unified
/// <see cref="ChargeWithSplitAsync"/>: capture the payment, then create transfers against it.
/// Razorpay does NOT support a re-usable named split — every charge supplies its own beneficiary
/// list. <see cref="CreateSplitAsync"/> therefore stores the rules client-side (in memory) so they
/// can be referenced by <see cref="ChargeWithSplitAsync"/>; consumers wanting persistence should
/// store them in their own DB and pass <see cref="ChargeWithSplitRequest.InlineRules"/> directly.
/// </remarks>
public sealed class RazorpayMarketplaceProvider : BhenguProviderBase, IMarketplaceProvider
{
    private const string SplitKeyPrefix = "razorpay:split:";
    private static readonly TimeSpan SplitTtl = TimeSpan.FromDays(365);

    private readonly RazorpayHttpClient _http;
    private readonly IBhenguDistributedCache _cache;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new marketplace provider bound to the supplied HTTP client, options, and distributed cache for split definitions.</summary>
    public RazorpayMarketplaceProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayMarketplaceProvider> logger,
        IBhenguDistributedCache cache)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _http = new RazorpayHttpClient(httpClient, options.Value, ProviderName, logger);
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public RazorpayMarketplaceProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayMarketplaceProvider> logger)
        : this(httpClient, options, logger, new InMemoryBhenguDistributedCache())
    {
    }

    /// <inheritdoc />
    public Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_sub_account", () => CreateSubAccountCoreAsync(request, ct), ct);
    }

    private async Task<SubAccount> CreateSubAccountCoreAsync(SubAccountRequest request, CancellationToken ct)
    {
        var body = new
        {
            email = request.ContactEmail,
            phone = request.Metadata?.GetValueOrDefault("phone"),
            legal_business_name = request.BusinessName,
            business_type = request.Metadata?.GetValueOrDefault("business_type") ?? "individual",
            contact_name = request.Metadata?.GetValueOrDefault("contact_name") ?? request.BusinessName,
            profile = new
            {
                category = request.Metadata?.GetValueOrDefault("category") ?? "ecommerce",
                subcategory = request.Metadata?.GetValueOrDefault("subcategory") ?? "ecommerce",
                addresses = new
                {
                    registered = new
                    {
                        street1 = request.Metadata?.GetValueOrDefault("street") ?? string.Empty,
                        city = request.Metadata?.GetValueOrDefault("city") ?? string.Empty,
                        state = request.Metadata?.GetValueOrDefault("state") ?? string.Empty,
                        country = request.Country.ToUpperInvariant(),
                        postal_code = request.Metadata?.GetValueOrDefault("postal_code") ?? string.Empty
                    }
                }
            }
        };

        var raw = await _http.SendAsync(HttpMethod.Post, "v2/accounts", body, ct, "CreateAccount", request.IdempotencyKey).ConfigureAwait(false);
        var account = RazorpayHttpClient.DeserialiseOrThrow<RazorpayLinkedAccount>(raw, ProviderName, "CreateAccount");

        Logger.LogInformation("Razorpay linked account created: {AccountId}", account.Id);

        return new SubAccount
        {
            Reference = account.Id ?? string.Empty,
            BusinessName = account.LegalBusinessName ?? request.BusinessName,
            ContactEmail = account.Email ?? request.ContactEmail,
            SettlementAccountToken = request.SettlementAccountToken,
            IsActive = string.Equals(account.Status, "activated", StringComparison.OrdinalIgnoreCase),
            // Hosted KYC URL — null when the API issues an instant-activate account.
            OnboardingUrl = account.ActivationFormUrl
        };
    }

    /// <inheritdoc />
    public Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAccountReference);
        return RunOperationAsync("get_sub_account", () => GetSubAccountCoreAsync(subAccountReference, ct), ct);
    }

    private async Task<SubAccount?> GetSubAccountCoreAsync(string subAccountReference, CancellationToken ct)
    {
        try
        {
            var raw = await _http.GetAsync($"v2/accounts/{Uri.EscapeDataString(subAccountReference)}", ct, "GetAccount").ConfigureAwait(false);
            var account = RazorpayHttpClient.DeserialiseOrThrow<RazorpayLinkedAccount>(raw, ProviderName, "GetAccount");

            return new SubAccount
            {
                Reference = account.Id ?? string.Empty,
                BusinessName = account.LegalBusinessName ?? string.Empty,
                ContactEmail = account.Email,
                SettlementAccountToken = null,
                IsActive = string.Equals(account.Status, "activated", StringComparison.OrdinalIgnoreCase),
                OnboardingUrl = account.ActivationFormUrl
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
#pragma warning disable CS1998 // intentionally async with no awaits — Razorpay has no public list endpoint, but the contract demands IAsyncEnumerable.
    public async IAsyncEnumerable<SubAccount> ListSubAccountsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Razorpay's v2 Accounts API doesn't expose a public list endpoint — accounts are looked up
        // by id. Consumers that need a roster should mirror it in their own DB on creation.
        Logger.LogDebug("Razorpay does not expose a public account-list endpoint; returning empty");
        yield break;
    }
#pragma warning restore CS1998

    /// <inheritdoc />
    public Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_split", () => CreateSplitCoreAsync(request, ct), ct);
    }

    private async Task<SplitDefinition> CreateSplitCoreAsync(SplitDefinitionRequest request, CancellationToken ct)
    {
        // Razorpay has no persisted split entity — we cache the rules and synthesise an opaque id.
        var reference = $"split_local_{Guid.NewGuid():N}";
        var split = new SplitDefinition
        {
            Reference = reference,
            Name = request.Name,
            Currency = request.Currency.ToUpperInvariant(),
            Rules = request.Rules
        };

        await _cache.SetAsync(SplitKeyPrefix + reference, split, SplitTtl, ct).ConfigureAwait(false);

        Logger.LogInformation("Razorpay split cached as {SplitId} ({RuleCount} beneficiaries)", reference, request.Rules.Count);
        return split;
    }

    /// <inheritdoc />
    public Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(splitReference);
        return RunOperationAsync("get_split",
            () => _cache.GetAsync<SplitDefinition>(SplitKeyPrefix + splitReference, ct),
            ct);
    }

    /// <inheritdoc />
    public Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Payment.Currency, () => ChargeWithSplitCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ChargeWithSplitCoreAsync(ChargeWithSplitRequest request, CancellationToken ct)
    {
        if (request.SplitReference is null && (request.InlineRules is null || request.InlineRules.Count == 0))
            throw new BhenguPaymentException(ProviderName, "Either SplitReference or InlineRules must be supplied");

        // Resolve rules.
        IReadOnlyList<SplitRule>? rules = request.InlineRules;
        if (rules is null && request.SplitReference is not null)
        {
            var cached = await _cache.GetAsync<SplitDefinition>(SplitKeyPrefix + request.SplitReference, ct).ConfigureAwait(false);
            rules = cached?.Rules;
        }
        if (rules is null || rules.Count == 0)
            throw new BhenguPaymentException(ProviderName, $"Split {request.SplitReference} not found and no inline rules supplied");

        var payment = request.Payment;
        var amountInPaise = (long)(payment.Amount * 100);
        var currency = payment.Currency.ToUpperInvariant();

        // Step 1: capture the underlying payment.
        var captureRaw = await _http.SendAsync(HttpMethod.Post,
            $"v1/payments/{Uri.EscapeDataString(payment.PaymentMethodToken)}/capture",
            new { amount = amountInPaise, currency }, ct, "CaptureForSplit", payment.IdempotencyKey).ConfigureAwait(false);
        var capture = RazorpayHttpClient.DeserialiseOrThrow<RazorpayCapture>(captureRaw, ProviderName, "CaptureForSplit");

        // Step 2: create transfers against the captured payment.
        var transfers = rules.Select(r => new
        {
            account = r.SubAccountReference,
            amount = ComputeShare(r, amountInPaise),
            currency,
            on_hold = 0
        }).ToArray();

        var transferRaw = await _http.SendAsync(HttpMethod.Post,
            $"v1/payments/{Uri.EscapeDataString(capture.Id ?? payment.PaymentMethodToken)}/transfers",
            new { transfers }, ct, "CreateTransfers", payment.IdempotencyKey).ConfigureAwait(false);

        Logger.LogInformation("Razorpay split captured: payment={PaymentId} beneficiaries={BeneficiaryCount}",
            capture.Id, rules.Count);

        return new PaymentResponse
        {
            GatewayReference = capture.Id ?? payment.PaymentMethodToken,
            Status = MapStatus(capture.Status),
            Amount = payment.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            Message = transferRaw
        };
    }

    private static long ComputeShare(SplitRule rule, long grossInPaise) => rule.ShareType switch
    {
        SplitShareType.FixedAmount => (long)((rule.Amount ?? 0m) * 100),
        SplitShareType.Percentage => (long)(grossInPaise * (rule.Percentage ?? 0m) / 100m),
        _ => 0L
    };

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "captured" or "paid" => PaymentStatus.Completed,
        "authorized" or "created" => PaymentStatus.Pending,
        "failed" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    // === Razorpay API response shapes (internal) ===

    private sealed class RazorpayLinkedAccount
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("phone")] public string? Phone { get; set; }
        [JsonPropertyName("legal_business_name")] public string? LegalBusinessName { get; set; }
        [JsonPropertyName("activation_form_milestone")] public string? ActivationFormUrl { get; set; }
    }

    private sealed class RazorpayCapture
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("entity")] public string? Entity { get; set; }
        [JsonPropertyName("amount")] public long Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
