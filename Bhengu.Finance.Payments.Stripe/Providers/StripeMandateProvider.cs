// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Mandate = Bhengu.Finance.Payments.Core.Models.Mandate.Mandate;
using StripeMandate = Stripe.Mandate;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe implementation of <see cref="IMandateProvider"/>. Bridges Stripe's <c>SetupIntent</c>
/// + <c>Mandate</c> + <c>PaymentIntent (off-session)</c> trio to the unified Bhengu mandate
/// contract. Suitable for SEPA Direct Debit, BACS Debit, and ACSS Debit flows where the payer
/// authorises the merchant to pull future debits.
/// </summary>
public sealed class StripeMandateProvider : BhenguProviderBase, IMandateProvider
{
    private readonly StripeOptions _options;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeMandateProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeMandateProvider> logger)
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
    public Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("create_mandate", async () =>
        {
            var requestOptions = BuildRequestOptions(request.IdempotencyKey);

            try
            {
                var siService = new SetupIntentService(_stripeClient);
                var siOptions = new SetupIntentCreateOptions
                {
                    Customer = request.CustomerId,
                    PaymentMethod = string.IsNullOrEmpty(request.BankAccountToken) ? null : request.BankAccountToken,
                    Usage = "off_session",
                    Confirm = !string.IsNullOrEmpty(request.BankAccountToken),
                    PaymentMethodTypes = new List<string> { "sepa_debit", "bacs_debit", "acss_debit" },
                    MandateData = new SetupIntentMandateDataOptions
                    {
                        CustomerAcceptance = new SetupIntentMandateDataCustomerAcceptanceOptions
                        {
                            Type = "online",
                            Online = new SetupIntentMandateDataCustomerAcceptanceOnlineOptions
                            {
                                // Caller is expected to capture these client-side; defaults are placeholders
                                // a hosted-flow provider would replace before sending to Stripe.
                                IpAddress = "0.0.0.0",
                                UserAgent = "BhenguPayments"
                            }
                        }
                    }
                };

                var si = await siService.CreateAsync(siOptions, requestOptions, ct).ConfigureAwait(false);
                Logger.LogInformation("Stripe SetupIntent created for mandate: {SiId} status={Status}", si.Id, si.Status);

                // If a Mandate was generated, fetch it so we have the final status; otherwise return the
                // SetupIntent identifier with the redirect URL (payer needs to authorise off-flow).
                StripeMandate? mandate = null;
                if (!string.IsNullOrEmpty(si.MandateId))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var mService = new MandateService(_stripeClient);
                        mandate = await mService.GetAsync(si.MandateId, cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (StripeException) { /* mandate may not yet be retrievable; fall back to SI */ }
                }

                return Map(si, mandate, request);
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "CreateMandate", Logger);
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        return RunOperationAsync("get_mandate", async () =>
        {
            try
            {
                // Stripe mandate IDs use the prefix "mandate_". SetupIntent IDs use "seti_".
                // We accept either: callers may have stored whichever ID we surfaced on CreateMandate.
                if (mandateReference.StartsWith("seti_", StringComparison.Ordinal))
                {
                    var siService = new SetupIntentService(_stripeClient);
                    var si = await siService.GetAsync(mandateReference, cancellationToken: ct).ConfigureAwait(false);
                    StripeMandate? mandate = null;
                    if (!string.IsNullOrEmpty(si.MandateId))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { mandate = await new MandateService(_stripeClient).GetAsync(si.MandateId, cancellationToken: ct).ConfigureAwait(false); }
                        catch (StripeException) { }
                    }
                    return (Mandate?)Map(si, mandate, null);
                }
                else
                {
                    var mService = new MandateService(_stripeClient);
                    var mandate = await mService.GetAsync(mandateReference, cancellationToken: ct).ConfigureAwait(false);
                    return (Mandate?)MapPlain(mandate);
                }
            }
            catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "GetMandate", Logger);
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        return RunOperationAsync("cancel_mandate", async () =>
        {
            try
            {
                // Stripe mandates cannot be "cancelled" directly via the API — the canonical way is to
                // cancel the underlying SetupIntent (if pending) or detach the underlying PaymentMethod.
                // For SetupIntent-style references we cancel the intent; for already-active mandates the
                // payer detaches the PM externally, so we return the current state without erroring.
                if (mandateReference.StartsWith("seti_", StringComparison.Ordinal))
                {
                    var siService = new SetupIntentService(_stripeClient);
                    try
                    {
                        var cancelled = await siService.CancelAsync(mandateReference, cancellationToken: ct).ConfigureAwait(false);
                        return Map(cancelled, null, null);
                    }
                    catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        // Already cancelled / already succeeded — idempotency contract: succeed silently.
                        ct.ThrowIfCancellationRequested();
                        var current = await siService.GetAsync(mandateReference, cancellationToken: ct).ConfigureAwait(false);
                        return Map(current, null, null);
                    }
                }
                else
                {
                    var mandate = await new MandateService(_stripeClient).GetAsync(mandateReference, cancellationToken: ct).ConfigureAwait(false);
                    if (mandate.PaymentMethodId is { Length: > 0 } pmId)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { await new PaymentMethodService(_stripeClient).DetachAsync(pmId, cancellationToken: ct).ConfigureAwait(false); }
                        catch (StripeException) { /* idempotent — already detached is fine */ }
                    }
                    ct.ThrowIfCancellationRequested();
                    var refetched = await new MandateService(_stripeClient).GetAsync(mandateReference, cancellationToken: ct).ConfigureAwait(false);
                    return MapPlain(refetched, forceCancelled: true);
                }
            }
            catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Idempotent: already-gone mandate is treated as already-cancelled.
                return new Mandate
                {
                    Reference = mandateReference,
                    CustomerId = string.Empty,
                    Status = MandateStatus.Cancelled,
                    AmountLimit = 0m,
                    Currency = "USD",
                    CancelledAt = DateTime.UtcNow
                };
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "CancelMandate", Logger);
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<PaymentResponse> ChargeMandateAsync(MandateChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var requestOptions = BuildRequestOptions(request.IdempotencyKey);

            try
            {
                // Resolve the PaymentMethod for this mandate. Caller passes us either:
                //  * a Stripe mandate ID (mandate_...) — fetch its PaymentMethodId
                //  * a SetupIntent ID (seti_...) — fetch the SI's mandate, then PM
                string? paymentMethodId = null;
                string? customerId = null;
                string? mandateId = null;
                if (request.MandateReference.StartsWith("seti_", StringComparison.Ordinal))
                {
                    var si = await new SetupIntentService(_stripeClient).GetAsync(request.MandateReference, cancellationToken: ct).ConfigureAwait(false);
                    paymentMethodId = si.PaymentMethodId;
                    customerId = si.CustomerId;
                    mandateId = si.MandateId;
                }
                else
                {
                    var mandate = await new MandateService(_stripeClient).GetAsync(request.MandateReference, cancellationToken: ct).ConfigureAwait(false);
                    paymentMethodId = mandate.PaymentMethodId;
                    mandateId = mandate.Id;
                    if (mandate.PaymentMethod?.CustomerId is { Length: > 0 } cid) customerId = cid;
                }

                if (string.IsNullOrEmpty(paymentMethodId))
                    throw new BhenguPaymentException(ProviderName, $"Mandate {request.MandateReference} has no associated PaymentMethod yet");

                ct.ThrowIfCancellationRequested();
                var piService = new PaymentIntentService(_stripeClient);
                var options = new PaymentIntentCreateOptions
                {
                    Amount = (long)(request.Amount * 100),
                    Currency = request.Currency.ToLowerInvariant(),
                    Customer = customerId,
                    PaymentMethod = paymentMethodId,
                    Mandate = mandateId,
                    Description = request.Description,
                    OffSession = true,
                    Confirm = true,
                    ConfirmationMethod = "automatic"
                };

                var intent = await piService.CreateAsync(options, requestOptions, ct).ConfigureAwait(false);
                Logger.LogInformation("Stripe Mandate charged: {IntentId} mandate={MandateRef} amount={Amount}", intent.Id, request.MandateReference, request.Amount);

                return new PaymentResponse
                {
                    GatewayReference = intent.Id,
                    Status = MapPaymentStatus(intent.Status),
                    Amount = request.Amount,
                    Currency = request.Currency,
                    ProcessedAt = DateTime.UtcNow,
                    Message = intent.Status
                };
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "ChargeMandate", Logger);
            }
        }, ct);
    }

    private static Mandate Map(SetupIntent si, StripeMandate? mandate, MandateRequest? request) => new()
    {
        Reference = mandate?.Id ?? si.Id,
        CustomerId = si.CustomerId ?? request?.CustomerId ?? string.Empty,
        Status = MapStatus(mandate?.Status, si.Status),
        AmountLimit = request?.AmountLimit ?? 0m,
        Currency = request?.Currency ?? "USD",
        AuthorisedAt = mandate is not null && mandate.Status == "active" ? si.Created : null,
        CancelledAt = (mandate?.Status == "inactive" || si.Status == "canceled") ? DateTime.UtcNow : null,
        AuthorisationUrl = si.NextAction?.RedirectToUrl?.Url
    };

    private static Mandate MapPlain(StripeMandate mandate, bool forceCancelled = false) => new()
    {
        Reference = mandate.Id,
        CustomerId = mandate.PaymentMethod?.CustomerId ?? string.Empty,
        Status = forceCancelled ? MandateStatus.Cancelled : MapStatus(mandate.Status, null),
        AmountLimit = 0m,
        Currency = "USD",
        AuthorisedAt = mandate.CustomerAcceptance?.AcceptedAt,
        CancelledAt = forceCancelled ? DateTime.UtcNow : (mandate.Status == "inactive" ? DateTime.UtcNow : null),
        AuthorisationUrl = null
    };

    private static MandateStatus MapStatus(string? mandateStatus, string? siStatus)
    {
        // Stripe Mandate.Status: "active", "inactive", "pending"
        // SetupIntent.Status: "requires_payment_method", "requires_confirmation", "requires_action", "processing", "canceled", "succeeded"
        if (!string.IsNullOrEmpty(mandateStatus))
        {
            return mandateStatus.ToLowerInvariant() switch
            {
                "active" => MandateStatus.Active,
                "inactive" => MandateStatus.Cancelled,
                "pending" => MandateStatus.Pending,
                _ => MandateStatus.Pending
            };
        }

        return siStatus?.ToLowerInvariant() switch
        {
            "succeeded" => MandateStatus.Active,
            "processing" => MandateStatus.Pending,
            "requires_payment_method" or "requires_confirmation" or "requires_action" => MandateStatus.Pending,
            "canceled" or "cancelled" => MandateStatus.Cancelled,
            _ => MandateStatus.Pending
        };
    }

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
