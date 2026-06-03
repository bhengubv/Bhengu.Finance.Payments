// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Stripe.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

namespace Bhengu.Finance.Payments.Stripe.Providers;

/// <summary>
/// Stripe implementation of <see cref="IThreeDSecureProvider"/>. Backed by Stripe PaymentIntents
/// with <c>request_three_d_secure = "any"</c>. The PaymentIntent's <c>next_action</c>
/// (<c>redirect_to_url</c>) is surfaced as the challenge URL.
/// </summary>
public sealed class StripeThreeDSecureProvider : IThreeDSecureProvider
{
    private readonly StripeOptions _options;
    private readonly ILogger<StripeThreeDSecureProvider> _logger;
    private readonly IStripeClient _stripeClient;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Stripe;

    /// <summary>Construct the provider. Throws <see cref="ProviderConfigurationException"/> if <see cref="StripeOptions.SecretKey"/> is unset.</summary>
    public StripeThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<StripeOptions> options,
        ILogger<StripeThreeDSecureProvider> logger)
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
    public async Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);

        var requestOptions = BuildRequestOptions(chargeIntent.IdempotencyKey);
        try
        {
            var service = new PaymentIntentService(_stripeClient);
            var options = new PaymentIntentCreateOptions
            {
                Amount = (long)(chargeIntent.Amount * 100),
                Currency = chargeIntent.Currency.ToLowerInvariant(),
                PaymentMethod = chargeIntent.PaymentMethodToken,
                Customer = chargeIntent.CustomerId,
                Description = chargeIntent.Description,
                ConfirmationMethod = "manual",
                Confirm = true,
                PaymentMethodOptions = new PaymentIntentPaymentMethodOptionsOptions
                {
                    Card = new PaymentIntentPaymentMethodOptionsCardOptions
                    {
                        RequestThreeDSecure = "any"
                    }
                },
                Metadata = chargeIntent.Metadata?.ToDictionary(k => k.Key, v => v.Value)
            };

            var intent = await service.CreateAsync(options, requestOptions, ct).ConfigureAwait(false);
            _logger.LogInformation("Stripe 3DS PaymentIntent created: {Id} status={Status}", intent.Id, intent.Status);

            return MapChallenge(intent);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "Start3DS");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    /// <inheritdoc />
    public async Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeReference);

        try
        {
            var service = new PaymentIntentService(_stripeClient);
            var intent = await service.GetAsync(challengeReference, cancellationToken: ct).ConfigureAwait(false);
            return MapChallenge(intent);
        }
        catch (StripeException ex)
        {
            throw TranslateException(ex, "Get3DS");
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stripe failed", ex);
        }
    }

    private static ThreeDSecureChallenge MapChallenge(PaymentIntent intent) => new()
    {
        Status = MapStatus(intent.Status, intent.NextAction),
        ChallengeReference = intent.Id,
        RedirectUrl = intent.NextAction?.RedirectToUrl?.Url,
        ChallengePayload = intent.ClientSecret,
        ProtocolVersion = null,
        DsTransactionId = null
    };

    private static ThreeDSecureStatus MapStatus(string? piStatus, PaymentIntentNextAction? next) => (piStatus?.ToLowerInvariant(), next?.Type?.ToLowerInvariant()) switch
    {
        ("succeeded", _) => ThreeDSecureStatus.Authenticated,
        ("requires_action", _) when next?.RedirectToUrl is not null => ThreeDSecureStatus.ChallengeRequired,
        ("requires_action", _) => ThreeDSecureStatus.ChallengeRequired,
        ("requires_confirmation", _) => ThreeDSecureStatus.Attempted,
        ("processing", _) => ThreeDSecureStatus.Attempted,
        ("canceled" or "cancelled", _) => ThreeDSecureStatus.Failed,
        ("requires_payment_method", _) => ThreeDSecureStatus.Failed,
        _ => ThreeDSecureStatus.NotRequired
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
