// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Runtime.CompilerServices;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Bhengu.Finance.Payments.Stripe.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using PaymentMethod = Bhengu.Finance.Payments.Core.Models.Vault.PaymentMethod;
using StripePaymentMethod = Stripe.PaymentMethod;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// READ-side Stripe vault provider. Implements <see cref="ITokenisationProvider"/> over Stripe's
/// <c>PaymentMethod</c> + <c>Customer</c> APIs — fetch, list, and delete saved payment methods.
/// </summary>
/// <remarks>
/// Raw-PAN write operations live on <see cref="StripeRawCardTokenisationProvider"/> — splitting the
/// PCI-DSS SAQ-D-bringing surface from the read-only one means consumers can't accidentally drift
/// into SAQ-D by calling a vault method on a provider they thought was read-only.
/// </remarks>
public sealed class StripeTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    private readonly StripeOptions _options;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeTokenisationProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeTokenisationProvider> logger)
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
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("get_payment_method", async () =>
        {
            try
            {
                var service = new PaymentMethodService(_stripeClient);
                var pm = await service.GetAsync(token, cancellationToken: ct).ConfigureAwait(false);
                return (PaymentMethod?)StripeTokenMapper.Map(pm, pm.CustomerId);
            }
            catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "GetPaymentMethod", Logger);
            }
        }, ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(
        string customerId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);

        // Stripe doesn't surface the default-payment-method on the PaymentMethod object itself —
        // it lives on Customer.InvoiceSettings.DefaultPaymentMethodId. One extra fetch upfront keeps
        // the IsDefault flag honest across the entire stream.
        string? defaultPmId = null;
        try
        {
            var customer = await new CustomerService(_stripeClient).GetAsync(customerId, cancellationToken: ct).ConfigureAwait(false);
            defaultPmId = customer.InvoiceSettings?.DefaultPaymentMethodId;
        }
        catch (StripeException)
        {
            // Non-fatal: list still works even if the customer fetch fails (deleted customer, no read scope, etc.)
        }

        var service = new PaymentMethodService(_stripeClient);
        var listOptions = new PaymentMethodListOptions { Customer = customerId, Type = "card", Limit = 100 };

        var paginator = service.ListAutoPagingAsync(listOptions, cancellationToken: ct);
        await foreach (var pm in paginator.ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            yield return StripeTokenMapper.Map(pm, pm.CustomerId, isDefault: pm.Id == defaultPmId);
        }
    }

    /// <inheritdoc />
    public Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("delete_payment_method", async () =>
        {
            try
            {
                var service = new PaymentMethodService(_stripeClient);
                var detached = await service.DetachAsync(token, cancellationToken: ct).ConfigureAwait(false);
                return detached is not null;
            }
            catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return false;
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "DeletePaymentMethod", Logger);
            }
        }, ct);
    }
}

/// <summary>
/// WRITE-side Stripe vault provider — accepts raw PAN and lands the consumer in PCI-DSS SAQ-D scope.
/// Implements <see cref="IRawCardTokenisationProvider"/> only; consumers who need to read the vault
/// resolve <see cref="StripeTokenisationProvider"/> alongside.
/// </summary>
/// <remarks>
/// <b>Strongly prefer client-side tokenisation</b> (Stripe Elements) which sends raw PAN directly
/// from the payer's browser to Stripe and returns a short-lived <c>pm_...</c> token to your backend.
/// This implementation exists for SAQ-D merchants and server-side migration flows only.
/// </remarks>
public sealed class StripeRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly StripeOptions _options;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeRawCardTokenisationProvider> logger)
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
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Card);
        return RunOperationAsync("tokenise", async () =>
        {
            var pmService = new PaymentMethodService(_stripeClient);
            var requestOptions = BuildRequestOptions(request.IdempotencyKey);

            try
            {
                // Step 1: create the PaymentMethod from raw card details
                var createOptions = new PaymentMethodCreateOptions
                {
                    Type = "card",
                    Card = new PaymentMethodCardOptions
                    {
                        Number = request.Card.CardNumber,
                        ExpMonth = request.Card.ExpiryMonth,
                        ExpYear = request.Card.ExpiryYear,
                        Cvc = request.Card.Cvv
                    },
                    BillingDetails = new PaymentMethodBillingDetailsOptions
                    {
                        Name = request.Card.CardholderName,
                        Address = (request.Card.BillingAddressLine1 is not null || request.Card.BillingPostalCode is not null || request.Card.BillingCountry is not null)
                            ? new AddressOptions
                            {
                                Line1 = request.Card.BillingAddressLine1,
                                PostalCode = request.Card.BillingPostalCode,
                                Country = request.Card.BillingCountry
                            }
                            : null
                    }
                };

                var created = await pmService.CreateAsync(createOptions, requestOptions, ct).ConfigureAwait(false);

                // Step 2: if no customer supplied, create one; then attach the PM to it
                var customerId = request.CustomerId;
                if (string.IsNullOrEmpty(customerId))
                {
                    ct.ThrowIfCancellationRequested();
                    var customerService = new CustomerService(_stripeClient);
                    var customerCreate = new CustomerCreateOptions
                    {
                        Name = request.DisplayName ?? request.Card.CardholderName,
                        PaymentMethod = created.Id
                    };
                    var customer = await customerService.CreateAsync(customerCreate, requestOptions, ct).ConfigureAwait(false);
                    customerId = customer.Id;
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    await pmService.AttachAsync(created.Id, new PaymentMethodAttachOptions { Customer = customerId }, requestOptions, ct).ConfigureAwait(false);
                }

                if (request.SetAsDefault)
                {
                    ct.ThrowIfCancellationRequested();
                    var customerService = new CustomerService(_stripeClient);
                    await customerService.UpdateAsync(customerId, new CustomerUpdateOptions
                    {
                        InvoiceSettings = new CustomerInvoiceSettingsOptions { DefaultPaymentMethod = created.Id }
                    }, requestOptions, ct).ConfigureAwait(false);
                }

                Logger.LogInformation("Stripe PaymentMethod tokenised: {PmId} customer={CustomerId}", created.Id, customerId);
                return StripeTokenMapper.Map(created, customerId, request.SetAsDefault, request.DisplayName);
            }
            catch (StripeException ex)
            {
                throw StripeExceptionTranslator.Translate(ex, ProviderName, "Tokenise", Logger);
            }
        }, ct);
    }

    private static RequestOptions? BuildRequestOptions(string? idempotencyKey) =>
        string.IsNullOrEmpty(idempotencyKey) ? null : new RequestOptions { IdempotencyKey = idempotencyKey };
}

/// <summary>Internal mapper from Stripe SDK PaymentMethod to the Bhengu vault descriptor.</summary>
internal static class StripeTokenMapper
{
    public static PaymentMethod Map(StripePaymentMethod pm, string? customerId, bool isDefault = false, string? displayName = null) =>
        new()
        {
            Token = pm.Id,
            CustomerId = customerId,
            Kind = MapKind(pm.Type),
            Brand = pm.Card?.Brand,
            Last4 = pm.Card?.Last4 ?? pm.SepaDebit?.Last4 ?? pm.BacsDebit?.Last4 ?? pm.UsBankAccount?.Last4 ?? pm.AcssDebit?.Last4,
            ExpiryMonth = pm.Card is not null ? (int)pm.Card.ExpMonth : null,
            ExpiryYear = pm.Card is not null ? (int)pm.Card.ExpYear : null,
            DisplayName = displayName,
            IsDefault = isDefault,
            CreatedAt = pm.Created
        };

    private static PaymentMethodKind MapKind(string? type) => type?.ToLowerInvariant() switch
    {
        "card" => PaymentMethodKind.Card,
        "sepa_debit" or "us_bank_account" or "bacs_debit" or "acss_debit" or "au_becs_debit" => PaymentMethodKind.BankAccount,
        "alipay" or "wechat_pay" or "apple_pay" or "google_pay" or "link" or "paypal" or "cashapp" => PaymentMethodKind.Wallet,
        _ => PaymentMethodKind.Other
    };
}
