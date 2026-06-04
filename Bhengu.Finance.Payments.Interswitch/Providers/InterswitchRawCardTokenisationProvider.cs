// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Bhengu.Finance.Payments.Interswitch.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Interswitch.Providers;

/// <summary>
/// WRITE-side Interswitch tokenisation provider — accepts raw PAN and vaults it into Interswitch's
/// card-on-file endpoint <c>POST /payment/v2/save-card</c>. Implementing this brings the consumer
/// into PCI-DSS SAQ-D scope; <b>strongly prefer the hosted Quickteller checkout</b> for any new
/// integration.
/// </summary>
public sealed class InterswitchRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly InterswitchHttpClient _http;
    private readonly InterswitchIdempotencyCache _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Interswitch;

    /// <summary>Construct a raw-card tokenisation provider. Designed to be registered via DI.</summary>
    public InterswitchRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<InterswitchOptions> options,
        ILogger<InterswitchRawCardTokenisationProvider> logger,
        InterswitchIdempotencyCache idempotency)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var opts = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(opts.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(opts.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(InterswitchOptions.ClientSecret)} is required");

        _http = new InterswitchHttpClient(httpClient, opts, Logger);
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
                "Interswitch save-card requires TokeniseRequest.CustomerId.");

        var body = new
        {
            customerId = request.CustomerId,
            cardPan = request.Card.CardNumber,
            expiryDate = $"{request.Card.ExpiryMonth:D2}{request.Card.ExpiryYear % 100:D2}",
            cvv2 = request.Card.Cvv,
            pinBlock = (string?)null,
            cardHolderName = request.Card.CardholderName,
            alias = request.DisplayName,
            defaultCard = request.SetAsDefault
        };

        var json = await _http.SendAsync(HttpMethod.Post, "payment/v2/save-card", body, "TokeniseSaveCard", ct).ConfigureAwait(false);
        var resp = JsonSerializer.Deserialize<InterswitchTokenisationProvider.InterswitchSavedCardResponse>(
            json, InterswitchHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "Interswitch save-card returned an empty body", "empty_response");

        if (string.IsNullOrWhiteSpace(resp.CardToken))
            throw new PaymentDeclinedException(ProviderName, resp.ResponseCode ?? "no_token",
                resp.ResponseDescription ?? "Interswitch did not return a card token.");

        return InterswitchTokenisationProvider.Map(resp, request.CustomerId, request.DisplayName, request.SetAsDefault);
    }
}
