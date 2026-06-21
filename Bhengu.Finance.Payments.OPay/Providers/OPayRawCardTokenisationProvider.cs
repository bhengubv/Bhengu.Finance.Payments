// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.OPay.Configuration;
using Bhengu.Finance.Payments.OPay.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OPay.Providers;

/// <summary>
/// WRITE-side OPay tokenisation — registers a saved bank-account (NUBAN + bankCode) under an
/// OPay user. OPay does not accept raw card details server-side; for card tokenisation use the
/// OPay-hosted cashier and persist the resulting <c>paymentToken</c> on the callback.
/// </summary>
/// <remarks>
/// Pass the bank-account number via <see cref="CardDetails.CardNumber"/> and the 3-digit CBN
/// bank code via <see cref="CardDetails.BillingAddressLine1"/>.
/// <para>UNVERIFIED: <c>api/v1/international/cashier/savedBankAccount/register</c> is not in OPay's
/// public Cashier docs; the signing scheme is the documented HMAC-SHA512 but the endpoint/payload
/// are unverified.</para>
/// </remarks>
public sealed class OPayRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly OPayHttpClient _http;
    private readonly OPayOptions _options;
    private readonly OPayIdempotencyCache _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.OPay;

    /// <summary>Construct a raw-card tokenisation provider. Designed to be registered via DI.</summary>
    public OPayRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<OPayOptions> options,
        ILogger<OPayRawCardTokenisationProvider> logger,
        OPayIdempotencyCache idempotency)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.PublicKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.PublicKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.SecretKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OPayOptions.MerchantId)} is required");

        _http = new OPayHttpClient(httpClient, _options, Logger);
    }

    /// <inheritdoc />
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, "tokenise",
            () => RunOperationAsync("tokenise", () => TokeniseCoreAsync(request, ct), ct), ct);
    }

    private async Task<PaymentMethod> TokeniseCoreAsync(TokeniseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerId))
            throw new PaymentDeclinedException(ProviderName, "missing_customer",
                "OPay tokenisation requires TokeniseRequest.CustomerId (OPay user id).");

        var bankCode = request.Card.BillingAddressLine1
            ?? throw new PaymentDeclinedException(ProviderName, "missing_bank_code",
                "OPay tokenisation requires bank code in CardDetails.BillingAddressLine1.");

        var body = new
        {
            publicKey = _options.PublicKey,
            country = _options.Country,
            sn = _options.MerchantId,
            userId = request.CustomerId,
            bankAccountNumber = request.Card.CardNumber,
            bankCode,
            accountHolderName = request.Card.CardholderName,
            alias = request.DisplayName,
            setAsDefault = request.SetAsDefault
        };

        var json = await _http.SendAsync(HttpMethod.Post, "api/v1/international/cashier/savedBankAccount/register",
            body, "Tokenise", ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<OPayTokenisationProvider.OPayResponseEnvelope<OPayTokenisationProvider.OPaySavedBankAccount>>(
            json, OPayHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "OPay save-bank-account returned an empty body", "empty_response");

        if (!string.Equals(resp.Code, "00000", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(resp.Data?.Token))
            throw new PaymentDeclinedException(ProviderName, resp.Code ?? "no_token", resp.Message);

        return OPayTokenisationProvider.Map(resp.Data, request.CustomerId, request.DisplayName, request.SetAsDefault);
    }
}
