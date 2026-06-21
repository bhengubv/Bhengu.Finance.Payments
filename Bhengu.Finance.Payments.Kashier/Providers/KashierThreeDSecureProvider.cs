// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Kashier.Providers;

/// <summary>
/// Kashier implementation of <see cref="IThreeDSecureProvider"/>. Kashier performs 3-D Secure inside its
/// <c>POST /checkout</c> flow — the response carries either an ACS redirect URL (issuer challenge required)
/// or a frictionless approval. The challenge state is later read via order reconciliation
/// (<c>GET /payments/orders/{merchantOrderId}</c>).
/// </summary>
/// <remarks>
/// Sources: www.kashier.io/docs/integration-guide and developers.kashier.io (Direct API integration "3D Secure
/// Handling"; order reconciliation). The exact field names Kashier returns for the ACS challenge inside
/// <c>response.card</c> are not fully documented publicly and are marked UNVERIFIED below.
/// </remarks>
public sealed class KashierThreeDSecureProvider : BhenguProviderBase, IThreeDSecureProvider
{
    private readonly HttpClient _httpClient;
    private readonly KashierOptions _options;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Kashier;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public KashierThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<KashierOptions> options,
        ILogger<KashierThreeDSecureProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(KashierOptions.MerchantId)} is required");

        KashierHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);
        return RunOperationAsync("start_3ds_authentication", async () =>
        {
            var requestThreeDs = !string.Equals(chargeIntent.Metadata?.GetValueOrDefault("request_3d_secure"), "false", StringComparison.OrdinalIgnoreCase);
            if (!requestThreeDs)
            {
                return new ThreeDSecureChallenge
                {
                    Status = ThreeDSecureStatus.NotRequired,
                    ChallengeReference = chargeIntent.IdempotencyKey ?? $"kashier-noscan-{Guid.NewGuid():N}"
                };
            }

            var orderId = chargeIntent.Metadata?.GetValueOrDefault("orderId") ?? $"kashier-3ds-{Guid.NewGuid():N}";
            var amount = chargeIntent.Amount.ToString("0.00", CultureInfo.InvariantCulture);
            var currency = string.IsNullOrWhiteSpace(chargeIntent.Currency) ? _options.Currency : chargeIntent.Currency.ToUpperInvariant();
            var key = string.IsNullOrWhiteSpace(_options.SecretKey) ? _options.ApiKey : _options.SecretKey;
            var hash = KashierPaymentProvider.ComputeOrderHash(_options.MerchantId, orderId, amount, currency, key);

            // 3DS is exercised through the standard checkout request; Kashier returns the ACS challenge when the
            // issuer requires a step-up.
            var requestBody = new Dictionary<string, object?>
            {
                ["merchantId"] = _options.MerchantId,
                ["orderId"] = orderId,
                ["amount"] = amount,
                ["currency"] = currency,
                ["hash"] = hash,
                ["shopper_reference"] = chargeIntent.CustomerId,
                ["cardToken"] = chargeIntent.PaymentMethodToken,
                ["merchantRedirect"] = _options.RedirectUrl,
                ["display"] = "en"
            };

            var responseBody = await KashierHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Post, "checkout",
                requestBody, "Start3DS", ct, chargeIntent.IdempotencyKey).ConfigureAwait(false);

            var parsed = JsonSerializer.Deserialize<KashierThreeDsResponse>(responseBody, KashierHttpClient.Json)
                ?? throw new ProviderUnavailableException(ProviderName, "Kashier 3DS start returned no payload");
            var response = parsed.Response;

            // Evaluate the transaction status (response.status), not the envelope status — the envelope's
            // "SUCCESS" only means the API call itself succeeded. An ACS URL always indicates an issuer challenge.
            var statusUpper = response?.Status?.ToUpperInvariant();
            var status = (statusUpper, hasAcs: !string.IsNullOrWhiteSpace(response?.AcsUrl)) switch
            {
                (_, true) => ThreeDSecureStatus.ChallengeRequired,
                ("PENDING_3DS" or "INPROGRESS", _) => ThreeDSecureStatus.ChallengeRequired,
                ({ } s, _) when s is "SUCCESS" or "APPROVED" => ThreeDSecureStatus.Authenticated,
                ("FAILED" or "DECLINED", _) => ThreeDSecureStatus.Failed,
                _ => ThreeDSecureStatus.Attempted
            };

            Logger.LogInformation("Kashier 3DS challenge: status={Status} acsUrl={HasAcs}", status, !string.IsNullOrEmpty(response?.AcsUrl));

            return new ThreeDSecureChallenge
            {
                Status = status,
                ChallengeReference = response?.TransactionId ?? response?.MerchantOrderId ?? orderId,
                RedirectUrl = response?.AcsUrl,
                ChallengePayload = response?.Pareq,
                ProtocolVersion = response?.ProtocolVersion ?? "2.x",
                DsTransactionId = response?.DsTransactionId
            };
        }, ct);
    }

    /// <inheritdoc/>
    public Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeReference);
        return RunOperationAsync("get_3ds_challenge", async () =>
        {
            try
            {
                // Poll the order reconciliation record for the latest authentication state.
                var responseBody = await KashierHttpClient.SendAsync(
                    _httpClient, Logger, HttpMethod.Get, $"payments/orders/{Uri.EscapeDataString(challengeReference)}", null, "Get3DS", ct).ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<KashierThreeDsResponse>(responseBody, KashierHttpClient.Json)?.Response;
                if (response is null)
                    return new ThreeDSecureChallenge { Status = ThreeDSecureStatus.ChallengeRequired, ChallengeReference = challengeReference };

                return new ThreeDSecureChallenge
                {
                    Status = (response.Status?.ToUpperInvariant()) switch
                    {
                        "SUCCESS" or "APPROVED" => ThreeDSecureStatus.Authenticated,
                        "FAILED" or "DECLINED" => ThreeDSecureStatus.Failed,
                        _ => ThreeDSecureStatus.ChallengeRequired
                    },
                    ChallengeReference = challengeReference,
                    DsTransactionId = response.DsTransactionId,
                    ProtocolVersion = response.ProtocolVersion
                };
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return new ThreeDSecureChallenge { Status = ThreeDSecureStatus.ChallengeRequired, ChallengeReference = challengeReference };
            }
        }, ct);
    }

    private sealed class KashierThreeDsResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("response")] public KashierThreeDsData? Response { get; set; }
    }

    // UNVERIFIED: Kashier's public docs do not enumerate the exact ACS/3DS fields returned inside response.card;
    // the names below are a best-effort mapping and are not sandbox-confirmed.
    private sealed class KashierThreeDsData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("merchantOrderId")] public string? MerchantOrderId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("acsUrl")] public string? AcsUrl { get; set; }
        [JsonPropertyName("pareq")] public string? Pareq { get; set; }
        [JsonPropertyName("dsTransactionId")] public string? DsTransactionId { get; set; }
        [JsonPropertyName("protocolVersion")] public string? ProtocolVersion { get; set; }
    }
}
