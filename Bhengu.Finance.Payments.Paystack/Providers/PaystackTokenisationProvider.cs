// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paystack.Providers;

/// <summary>
/// Paystack implementation of <see cref="ITokenisationProvider"/>. Wraps Paystack's Customer and
/// Charge endpoints to vault payment-method <em>authorization codes</em>.
/// </summary>
/// <remarks>
/// <para>Paystack's tokenisation model is different from Stripe's. The vault entity is the
/// <c>authorization_code</c> returned alongside a successful Verify response. There is no first-class
/// "create card token" REST endpoint — a customer must complete at least one charge for the
/// authorization to be reusable.</para>
/// <para>To work around this in server-side tokenisation flows the SDK uses Paystack's
/// <c>/charge</c> endpoint with the raw card payload. Merchants that cannot legally handle raw PAN
/// should instead rely on Paystack Inline / Popup on the client and call
/// <see cref="GetPaymentMethodAsync"/> with the authorization code their backend receives.</para>
/// </remarks>
public sealed class PaystackTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paystack;

    /// <summary>Construct a tokenisation provider. Designed to be registered via DI.</summary>
    public PaystackTokenisationProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        PaystackHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("get_payment_method", () => GetPaymentMethodCoreAsync(token, ct), ct);
    }

    private async Task<PaymentMethod?> GetPaymentMethodCoreAsync(string token, CancellationToken ct)
    {
        // Paystack does not have a single "get authorization" endpoint — but the authorization is
        // surfaced via Customer:fetch. The token itself is opaque; without a customer side reference
        // we attempt the customer endpoint keyed on the token. If the call fails the method is gone.
        try
        {
            var customerListBody = await PaystackHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, "customer?perPage=100", null, "GetPaymentMethod", ct).ConfigureAwait(false);
            var customers = JsonSerializer.Deserialize<PaystackCustomerListResponse>(customerListBody, PaystackHttpClient.Json);
            if (customers?.Data is null) return null;

            foreach (var customer in customers.Data)
            {
                if (customer.Authorizations is null) continue;
                foreach (var auth in customer.Authorizations)
                {
                    if (string.Equals(auth.AuthorizationCode, token, StringComparison.Ordinal))
                        return MapAuthorization(auth, customer.CustomerCode);
                }
            }
            return null;
        }
        catch (PaymentDeclinedException)
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(customerId);

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Get, $"customer/{Uri.EscapeDataString(customerId)}", null, "ListPaymentMethods", ct).ConfigureAwait(false);
        var customer = JsonSerializer.Deserialize<PaystackCustomerResponse>(responseBody, PaystackHttpClient.Json);

        if (customer?.Data?.Authorizations is null)
            yield break;

        foreach (var auth in customer.Data.Authorizations)
        {
            ct.ThrowIfCancellationRequested();
            yield return MapAuthorization(auth, customer.Data.CustomerCode);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        return RunOperationAsync("delete_payment_method", () => DeletePaymentMethodCoreAsync(token, ct), ct);
    }

    private async Task<bool> DeletePaymentMethodCoreAsync(string token, CancellationToken ct)
    {
        try
        {
            var body = new { authorization_code = token };
            await PaystackHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "customer/deactivate_authorization", body, "DeletePaymentMethod", ct).ConfigureAwait(false);
            return true;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return false;
        }
    }

    internal static PaymentMethod MapAuthorization(PaystackAuthorization auth, string? customerCode) => new()
    {
        Token = auth.AuthorizationCode ?? string.Empty,
        CustomerId = customerCode,
        Kind = string.Equals(auth.Channel, "bank", StringComparison.OrdinalIgnoreCase)
            ? PaymentMethodKind.BankAccount
            : PaymentMethodKind.Card,
        Brand = auth.Brand,
        Last4 = auth.Last4,
        ExpiryMonth = int.TryParse(auth.ExpMonth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var em) ? em : null,
        ExpiryYear = int.TryParse(auth.ExpYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ey) ? ey : null,
        IsDefault = false,
        CreatedAt = null
    };

    // === Paystack API shapes (internal) ===

    internal sealed class PaystackCustomerResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackCustomerData? Data { get; set; }
    }

    internal sealed class PaystackCustomerListResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("data")] public List<PaystackCustomerData>? Data { get; set; }
    }

    internal sealed class PaystackCustomerData
    {
        [JsonPropertyName("customer_code")] public string? CustomerCode { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("authorizations")] public List<PaystackAuthorization>? Authorizations { get; set; }
    }

    internal sealed class PaystackChargeResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackChargeData? Data { get; set; }
    }

    internal sealed class PaystackChargeData
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("authorization")] public PaystackAuthorization? Authorization { get; set; }
    }

    internal sealed class PaystackAuthorization
    {
        [JsonPropertyName("authorization_code")] public string? AuthorizationCode { get; set; }
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("last4")] public string? Last4 { get; set; }
        [JsonPropertyName("exp_month")] public string? ExpMonth { get; set; }
        [JsonPropertyName("exp_year")] public string? ExpYear { get; set; }
        [JsonPropertyName("channel")] public string? Channel { get; set; }
    }
}

/// <summary>
/// PCI-DSS SAQ-D-scope Paystack tokenisation. Submits raw card details to Paystack's
/// <c>/charge</c> endpoint and returns the resulting authorization-code. Prefer Paystack Inline
/// / Popup on the client so the payer's browser sends raw PAN directly to Paystack and your
/// server only sees the short-lived authorization-code.
/// </summary>
public sealed class PaystackRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;
    private readonly PaystackIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paystack;

    /// <summary>Construct a raw-card tokenisation provider. Designed to be registered via DI.</summary>
    public PaystackRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackRawCardTokenisationProvider> logger,
        PaystackIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        PaystackHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("tokenise",
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => TokeniseCoreAsync(request, ct)),
            ct);
    }

    private async Task<PaymentMethod> TokeniseCoreAsync(TokeniseRequest request, CancellationToken ct)
    {
        // Step 1 — resolve / create the customer wrapper. Paystack requires every tokenisation to
        // be attached to a customer (identified by email).
        var customerEmail = request.CustomerId ?? _options.DefaultEmail
            ?? throw new PaymentDeclinedException(ProviderName, "missing_customer",
                "Paystack tokenisation requires TokeniseRequest.CustomerId (customer e-mail) or PaystackOptions.DefaultEmail.");

        var customerBody = new
        {
            email = customerEmail,
            first_name = (string?)request.Card.CardholderName?.Split(' ', 2)[0],
            last_name = (string?)(request.Card.CardholderName?.Contains(' ', StringComparison.Ordinal) == true
                ? request.Card.CardholderName.Split(' ', 2)[1]
                : null)
        };

        var customerResponseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "customer", customerBody, "TokeniseCustomerCreate", ct).ConfigureAwait(false);
        var customer = JsonSerializer.Deserialize<PaystackTokenisationProvider.PaystackCustomerResponse>(customerResponseBody, PaystackHttpClient.Json);
        var customerCode = customer?.Data?.CustomerCode ?? customerEmail;

        // Step 2 — submit raw card to /charge. Paystack returns an authorization_code we can reuse.
        var chargeBody = new
        {
            email = customerEmail,
            amount = 5000L, // R50 / NGN50 pre-auth — Paystack discards no-pin tokenisation if amount = 0.
            card = new
            {
                number = request.Card.CardNumber,
                cvv = request.Card.Cvv,
                expiry_month = request.Card.ExpiryMonth.ToString("D2", CultureInfo.InvariantCulture),
                expiry_year = request.Card.ExpiryYear.ToString(CultureInfo.InvariantCulture)
            },
            metadata = new
            {
                tokenisation_only = true,
                display_name = request.DisplayName
            }
        };

        var chargeResponseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "charge", chargeBody, "TokeniseCharge", ct).ConfigureAwait(false);
        var charge = JsonSerializer.Deserialize<PaystackTokenisationProvider.PaystackChargeResponse>(chargeResponseBody, PaystackHttpClient.Json);

        var auth = charge?.Data?.Authorization
            ?? throw new BhenguPaymentException(ProviderName, "Paystack did not return an authorization on charge", "no_authorization");

        if (string.IsNullOrWhiteSpace(auth.AuthorizationCode))
            throw new PaymentDeclinedException(ProviderName, charge?.Data?.Status ?? "no_authorization_code",
                charge?.Message ?? "Paystack rejected the card and returned no reusable authorization code.");

        Logger.LogInformation("Paystack tokenised payment method for customer {CustomerCode}: {AuthCode}",
            customerCode, auth.AuthorizationCode);

        return new PaymentMethod
        {
            Token = auth.AuthorizationCode,
            CustomerId = customerCode,
            Kind = PaymentMethodKind.Card,
            Brand = auth.Brand,
            Last4 = auth.Last4,
            ExpiryMonth = int.TryParse(auth.ExpMonth, NumberStyles.Integer, CultureInfo.InvariantCulture, out var em) ? em : null,
            ExpiryYear = int.TryParse(auth.ExpYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ey) ? ey : null,
            DisplayName = request.DisplayName,
            IsDefault = request.SetAsDefault,
            CreatedAt = DateTime.UtcNow
        };
    }
}
