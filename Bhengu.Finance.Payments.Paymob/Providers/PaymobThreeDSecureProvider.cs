// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Bhengu.Finance.Payments.Paymob.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paymob.Providers;

/// <summary>
/// Paymob implementation of <see cref="IThreeDSecureProvider"/>. Backed by Paymob's Accept
/// payment-key API with <c>request_3d_secure = true</c>. The challenge surface is the Paymob
/// iframe URL (or a direct issuer ACSURL where the integration is configured for direct
/// merchant-hosted 3DS).
/// </summary>
/// <remarks>
/// <para>Paymob always returns a payment-token from <c>/api/acceptance/payment_keys</c>; the
/// authentication outcome is only finalised once the payer has completed the iframe flow and
/// Paymob fires a TRANSACTION callback. <see cref="GetChallengeAsync"/> polls the transaction
/// inquiry endpoint to surface the latest known status.</para>
/// <para>Frictionless flow: pass <c>request_3d_secure=false</c> via the <c>PaymentRequest.Metadata</c>
/// to bypass 3DS where the merchant has a low-risk indicator on file. The provider then returns
/// <see cref="ThreeDSecureStatus.NotRequired"/>.</para>
/// </remarks>
public sealed class PaymobThreeDSecureProvider : BhenguProviderBase, IThreeDSecureProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaymobOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paymob;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PaymobThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<PaymobOptions> options,
        ILogger<PaymobThreeDSecureProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaymobOptions.ApiKey)} is required");

        PaymobHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public async Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "3ds.start");

        // Frictionless escape hatch — merchant explicitly opted out of 3DS.
        var requestThreeDs = !string.Equals(chargeIntent.Metadata?.GetValueOrDefault("request_3d_secure"), "false", StringComparison.OrdinalIgnoreCase);
        if (!requestThreeDs)
        {
            return new ThreeDSecureChallenge
            {
                Status = ThreeDSecureStatus.NotRequired,
                ChallengeReference = chargeIntent.IdempotencyKey ?? $"paymob-noscapen-{Guid.NewGuid():N}"
            };
        }

        var integrationId = chargeIntent.Metadata?.GetValueOrDefault("integration_id") is { Length: > 0 } iidStr
                && int.TryParse(iidStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iid)
            ? iid
            : _options.IntegrationId;
        var iframeId = chargeIntent.Metadata?.GetValueOrDefault("iframe_id") is { Length: > 0 } ifStr
                && int.TryParse(ifStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ifv)
            ? ifv
            : _options.IframeId;

        if (integrationId <= 0)
            throw new PaymentDeclinedException(ProviderName, "missing_integration_id",
                "Paymob 3DS requires an 'integration_id' on PaymentRequest.Metadata or PaymobOptions.IntegrationId.");

        var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);

        var amountCents = (long)(chargeIntent.Amount * 100);
        var currency = string.IsNullOrWhiteSpace(chargeIntent.Currency) ? _options.Currency : chargeIntent.Currency.ToUpperInvariant();

        // Step 1 — create order
        var orderJson = await PaymobHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "api/ecommerce/orders", new
        {
            auth_token = authToken,
            delivery_needed = false,
            amount_cents = amountCents,
            currency,
            items = Array.Empty<object>()
        }, "3ds.CreateOrder", ct).ConfigureAwait(false);

        var order = JsonSerializer.Deserialize<OrderResponse>(orderJson, PaymobHttpClient.Json)
            ?? throw new ProviderUnavailableException(ProviderName, "Paymob order creation returned no payload");
        if (order.Id is null or 0)
            throw new ProviderUnavailableException(ProviderName, "Paymob order creation returned no id");

        // Step 2 — payment-key with 3DS forced on
        var keyJson = await PaymobHttpClient.SendAsync(_httpClient, Logger, HttpMethod.Post, "api/acceptance/payment_keys", new
        {
            auth_token = authToken,
            amount_cents = amountCents,
            expiration = 3600,
            order_id = order.Id,
            billing_data = BuildBillingPayload(chargeIntent),
            currency,
            integration_id = integrationId,
            lock_order_when_paid = true,
            request_3d_secure = true
        }, "3ds.CreatePaymentKey", ct).ConfigureAwait(false);

        var keyResponse = JsonSerializer.Deserialize<PaymentKeyResponse>(keyJson, PaymobHttpClient.Json);
        var paymentKey = keyResponse?.Token;
        if (string.IsNullOrEmpty(paymentKey))
            throw new ProviderUnavailableException(ProviderName, "Paymob payment_keys returned no token");

        var iframeUrl = iframeId > 0
            ? $"https://accept.paymob.com/api/acceptance/iframes/{iframeId}?payment_token={paymentKey}"
            : null;

        Logger.LogInformation("Paymob 3DS challenge started: order={OrderId} iframe={Iframe}", order.Id, iframeId);

        return new ThreeDSecureChallenge
        {
            Status = ThreeDSecureStatus.ChallengeRequired,
            ChallengeReference = order.Id!.Value.ToString(CultureInfo.InvariantCulture),
            RedirectUrl = iframeUrl,
            ChallengePayload = paymentKey,
            ProtocolVersion = "2.x"
        };
    }

    /// <inheritdoc/>
    public async Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "3ds.get");
        var authToken = await PaymobHttpClient.AuthenticateAsync(_httpClient, Logger, _options, ct).ConfigureAwait(false);

        try
        {
            // Inquire the transaction associated with the order — Paymob exposes it via
            // /api/ecommerce/orders/transaction_inquiry once the order has a settled txn.
            var responseBody = await PaymobHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "api/ecommerce/orders/transaction_inquiry",
                new { auth_token = authToken, order_id = challengeReference },
                "3ds.Inquire", ct).ConfigureAwait(false);

            var inquiry = JsonSerializer.Deserialize<TransactionInquiry>(responseBody, PaymobHttpClient.Json);
            var status = MapStatus(inquiry);
            return new ThreeDSecureChallenge
            {
                Status = status,
                ChallengeReference = challengeReference,
                DsTransactionId = inquiry?.TransactionId?.ToString(CultureInfo.InvariantCulture),
                ProtocolVersion = "2.x"
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            // No transaction yet → challenge still in flight.
            return new ThreeDSecureChallenge
            {
                Status = ThreeDSecureStatus.ChallengeRequired,
                ChallengeReference = challengeReference
            };
        }
    }

    private static ThreeDSecureStatus MapStatus(TransactionInquiry? inquiry) => inquiry switch
    {
        null => ThreeDSecureStatus.ChallengeRequired,
        { Success: true, ThreeDSecure: true } => ThreeDSecureStatus.Authenticated,
        { Success: true } => ThreeDSecureStatus.Attempted,
        { Pending: true } => ThreeDSecureStatus.ChallengeRequired,
        { Success: false } => ThreeDSecureStatus.Failed,
        _ => ThreeDSecureStatus.ChallengeRequired
    };

    private static object BuildBillingPayload(PaymentRequest request) => new
    {
        email = request.Metadata?.GetValueOrDefault("email") ?? "na@na.na",
        first_name = request.Metadata?.GetValueOrDefault("first_name") ?? "NA",
        last_name = request.Metadata?.GetValueOrDefault("last_name") ?? "NA",
        phone_number = request.Metadata?.GetValueOrDefault("phone_number") ?? "+20000000000",
        apartment = "NA",
        floor = "NA",
        street = "NA",
        building = "NA",
        shipping_method = "NA",
        postal_code = "NA",
        city = "NA",
        country = "NA",
        state = "NA"
    };

    private sealed class OrderResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
    }

    private sealed class PaymentKeyResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
    }

    private sealed class TransactionInquiry
    {
        [JsonPropertyName("id")] public long? TransactionId { get; set; }
        [JsonPropertyName("success")] public bool? Success { get; set; }
        [JsonPropertyName("pending")] public bool? Pending { get; set; }
        [JsonPropertyName("is_3d_secure")] public bool? ThreeDSecure { get; set; }
    }
}
