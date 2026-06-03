// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Dispute = Bhengu.Finance.Payments.Core.Models.Dispute.Dispute;
using DisputeEvidence = Bhengu.Finance.Payments.Core.Models.Dispute.DisputeEvidence;
using StripeDispute = Stripe.Dispute;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe implementation of <see cref="IDisputeProvider"/>. Wraps Stripe's <c>Dispute</c> API
/// to enumerate disputes, submit evidence, and close (accept) disputes.
/// </summary>
public sealed class StripeDisputeProvider : IDisputeProvider
{
    private readonly StripeOptions _options;
    private readonly ILogger<StripeDisputeProvider> _logger;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeDisputeProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeDisputeProvider> logger)
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
    public async Task<Dispute?> GetDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(disputeReference);

        try
        {
            var service = new DisputeService(_stripeClient);
            var dispute = await service.GetAsync(disputeReference, cancellationToken: ct).ConfigureAwait(false);
            return Map(dispute);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "GetDispute");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Dispute>> ListDisputesAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
    {
        try
        {
            var service = new DisputeService(_stripeClient);
            var listOptions = new DisputeListOptions
            {
                Limit = 100,
                Created = (fromUtc is null && toUtc is null) ? null : new DateRangeOptions
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
            throw TranslateException(ex, "ListDisputes");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Dispute> SubmitEvidenceAsync(string disputeReference, DisputeEvidence evidence, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(disputeReference);
        ArgumentNullException.ThrowIfNull(evidence);

        try
        {
            var service = new DisputeService(_stripeClient);
            var stripeEvidence = new DisputeEvidenceOptions
            {
                UncategorizedText = evidence.Explanation,
                CustomerName = evidence.CustomerName,
                CustomerEmailAddress = evidence.CustomerEmailAddress,
                BillingAddress = evidence.BillingAddress,
                ShippingAddress = evidence.ShippingAddress,
                ShippingCarrier = evidence.ShippingCarrier,
                ShippingTrackingNumber = evidence.ShippingTrackingNumber,
                ShippingDate = evidence.ShippingDate?.ToString("yyyy-MM-dd"),
                ShippingDocumentation = evidence.FileReferences?.FirstOrDefault(),
                UncategorizedFile = evidence.FileReferences is { Count: > 1 } refs ? refs[1] : null
            };

            // Allow consumers to push Stripe-specific evidence fields verbatim.
            if (evidence.ProviderEvidenceFields is { Count: > 0 })
            {
                foreach (var (key, value) in evidence.ProviderEvidenceFields)
                {
                    ApplyProviderField(stripeEvidence, key, value);
                }
            }

            var updateOptions = new DisputeUpdateOptions
            {
                Evidence = stripeEvidence,
                Submit = true
            };

            var updated = await service.UpdateAsync(disputeReference, updateOptions, cancellationToken: ct).ConfigureAwait(false);
            _logger.LogInformation("Stripe Dispute evidence submitted: {DisputeId} status={Status}", updated.Id, updated.Status);
            return Map(updated);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "SubmitDisputeEvidence");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<Dispute> AcceptDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(disputeReference);

        try
        {
            var service = new DisputeService(_stripeClient);
            var closed = await service.CloseAsync(disputeReference, cancellationToken: ct).ConfigureAwait(false);
            _logger.LogInformation("Stripe Dispute accepted: {DisputeId} status={Status}", closed.Id, closed.Status);
            return Map(closed);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "AcceptDispute");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    private static Dispute Map(StripeDispute d) => new()
    {
        Reference = d.Id,
        ChargeReference = d.ChargeId ?? d.PaymentIntentId ?? string.Empty,
        Amount = d.Amount / 100m,
        Currency = (d.Currency ?? "usd").ToUpperInvariant(),
        Status = MapStatus(d.Status),
        ReasonCode = d.Reason,
        ReasonDescription = d.Reason,
        OpenedAt = d.Created,
        EvidenceDueBy = d.EvidenceDetails?.DueBy,
        ChargebackFee = d.BalanceTransactions?.FirstOrDefault()?.Fee / 100m
    };

    private static DisputeStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "needs_response" or "warning_needs_response" => DisputeStatus.NeedsResponse,
        "under_review" or "warning_under_review" => DisputeStatus.UnderReview,
        "won" or "warning_closed" => DisputeStatus.Won,
        "lost" => DisputeStatus.Lost,
        "charge_refunded" => DisputeStatus.Accepted,
        _ => DisputeStatus.NeedsResponse
    };

    private static void ApplyProviderField(DisputeEvidenceOptions evidence, string key, string value)
    {
        // Map common Stripe field names verbatim; unknown keys are dropped (logged at caller's discretion).
        switch (key)
        {
            case "access_activity_log": evidence.AccessActivityLog = value; break;
            case "cancellation_policy": evidence.CancellationPolicy = value; break;
            case "cancellation_policy_disclosure": evidence.CancellationPolicyDisclosure = value; break;
            case "cancellation_rebuttal": evidence.CancellationRebuttal = value; break;
            case "customer_communication": evidence.CustomerCommunication = value; break;
            case "customer_purchase_ip": evidence.CustomerPurchaseIp = value; break;
            case "customer_signature": evidence.CustomerSignature = value; break;
            case "duplicate_charge_documentation": evidence.DuplicateChargeDocumentation = value; break;
            case "duplicate_charge_explanation": evidence.DuplicateChargeExplanation = value; break;
            case "duplicate_charge_id": evidence.DuplicateChargeId = value; break;
            case "product_description": evidence.ProductDescription = value; break;
            case "receipt": evidence.Receipt = value; break;
            case "refund_policy": evidence.RefundPolicy = value; break;
            case "refund_policy_disclosure": evidence.RefundPolicyDisclosure = value; break;
            case "refund_refusal_explanation": evidence.RefundRefusalExplanation = value; break;
            case "service_date": evidence.ServiceDate = value; break;
            case "service_documentation": evidence.ServiceDocumentation = value; break;
            case "uncategorized_text": evidence.UncategorizedText = value; break;
            case "uncategorized_file": evidence.UncategorizedFile = value; break;
            default: break;
        }
    }

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
