// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using PaymentMethod = Bhengu.Finance.Payments.Core.Models.Vault.PaymentMethod;
using StripePaymentMethod = Stripe.PaymentMethod;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe implementation of <see cref="ITokenisationProvider"/>. Backed by the Stripe
/// <c>PaymentMethod</c> and <c>Customer</c> APIs — tokenises raw card details, attaches them
/// to a Stripe Customer for re-use, and exposes vault list / fetch / delete operations.
/// </summary>
/// <remarks>
/// PCI scope: passing raw PAN through your server lands you in PCI-DSS SAQ-D. The Stripe-recommended
/// pattern is to tokenise client-side via Stripe Elements and pass the resulting <c>pm_...</c> token
/// to <c>ProcessPaymentAsync</c>. This implementation exists for SAQ-D merchants and for server-side
/// migration flows.
/// </remarks>
public sealed class StripeTokenisationProvider : ITokenisationProvider
{
    private readonly StripeOptions _options;
    private readonly ILogger<StripeTokenisationProvider> _logger;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeTokenisationProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeTokenisationProvider> logger)
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
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Card);

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
                await pmService.AttachAsync(created.Id, new PaymentMethodAttachOptions { Customer = customerId }, requestOptions, ct).ConfigureAwait(false);
            }

            if (request.SetAsDefault)
            {
                var customerService = new CustomerService(_stripeClient);
                await customerService.UpdateAsync(customerId, new CustomerUpdateOptions
                {
                    InvoiceSettings = new CustomerInvoiceSettingsOptions { DefaultPaymentMethod = created.Id }
                }, requestOptions, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Stripe PaymentMethod tokenised: {PmId} customer={CustomerId}", created.Id, customerId);
            return Map(created, customerId, request.SetAsDefault, request.DisplayName);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "Tokenise");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        try
        {
            var service = new PaymentMethodService(_stripeClient);
            var pm = await service.GetAsync(token, cancellationToken: ct).ConfigureAwait(false);
            return Map(pm, pm.CustomerId);
        }
        catch (StripeException ex) when (ex.HttpStatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "GetPaymentMethod");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);

        try
        {
            var service = new PaymentMethodService(_stripeClient);
            var listOptions = new PaymentMethodListOptions { Customer = customerId, Type = "card", Limit = 100 };
            var page = await service.ListAsync(listOptions, cancellationToken: ct).ConfigureAwait(false);

            // Stripe doesn't surface the default-payment-method on the PaymentMethod object itself —
            // it lives on Customer.InvoiceSettings.DefaultPaymentMethodId. One extra fetch keeps the
            // IsDefault flag honest.
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

            return page.Data.Select(pm => Map(pm, pm.CustomerId, isDefault: pm.Id == defaultPmId)).ToList();
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "ListPaymentMethods");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

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
            throw TranslateException(ex, "DeletePaymentMethod");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    private static PaymentMethod Map(StripePaymentMethod pm, string? customerId, bool isDefault = false, string? displayName = null) =>
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
