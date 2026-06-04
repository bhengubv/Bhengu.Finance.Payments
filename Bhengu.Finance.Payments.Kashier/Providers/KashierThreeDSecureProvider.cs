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
using Bhengu.Finance.Payments.Kashier.Configuration;
using Bhengu.Finance.Payments.Kashier.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Kashier.Providers;

/// <summary>
/// Kashier implementation of <see cref="IThreeDSecureProvider"/>. Backed by Kashier's
/// <c>/orders/{id}/payments</c> with the <c>3ds=true</c> flag — the response carries either
/// an ACSURL (challenge required) or a CAVV (frictionless) depending on issuer policy.
/// </summary>
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
    public async Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "3ds.start");

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

        var requestBody = new
        {
            amount,
            currency,
            shopperReference = chargeIntent.CustomerId,
            cardData = chargeIntent.PaymentMethodToken,
            description = chargeIntent.Description,
            ThreeDs = true
        };

        var responseBody = await KashierHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, $"orders/{Uri.EscapeDataString(orderId)}/payments",
            requestBody, "Start3DS", ct, chargeIntent.IdempotencyKey).ConfigureAwait(false);

        var response = JsonSerializer.Deserialize<KashierThreeDsResponse>(responseBody, KashierHttpClient.Json)?.Response
            ?? throw new ProviderUnavailableException(ProviderName, "Kashier 3DS start returned no payload");

        var statusUpper = response.Status?.ToUpperInvariant();
        var status = (statusUpper, hasAcs: !string.IsNullOrWhiteSpace(response.AcsUrl)) switch
        {
            ({ } s, _) when s is "SUCCESS" or "APPROVED" => ThreeDSecureStatus.Authenticated,
            (_, true) => ThreeDSecureStatus.ChallengeRequired,
            ("PENDING_3DS" or "INPROGRESS", _) => ThreeDSecureStatus.ChallengeRequired,
            ("FAILED" or "DECLINED", _) => ThreeDSecureStatus.Failed,
            _ => ThreeDSecureStatus.Attempted
        };

        Logger.LogInformation("Kashier 3DS challenge: status={Status} acsUrl={HasAcs}", status, !string.IsNullOrEmpty(response.AcsUrl));

        return new ThreeDSecureChallenge
        {
            Status = status,
            ChallengeReference = response.TransactionId ?? orderId,
            RedirectUrl = response.AcsUrl,
            ChallengePayload = response.Pareq,
            ProtocolVersion = response.ProtocolVersion ?? "2.x",
            DsTransactionId = response.DsTransactionId
        };
    }

    /// <inheritdoc/>
    public async Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(challengeReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "3ds.get");

        try
        {
            var responseBody = await KashierHttpClient.SendAsync(
                _httpClient, Logger, HttpMethod.Get, $"payments/{Uri.EscapeDataString(challengeReference)}", null, "Get3DS", ct).ConfigureAwait(false);
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
    }

    private sealed class KashierThreeDsResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("response")] public KashierThreeDsData? Response { get; set; }
    }

    private sealed class KashierThreeDsData
    {
        [JsonPropertyName("transactionId")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("acsUrl")] public string? AcsUrl { get; set; }
        [JsonPropertyName("pareq")] public string? Pareq { get; set; }
        [JsonPropertyName("dsTransactionId")] public string? DsTransactionId { get; set; }
        [JsonPropertyName("protocolVersion")] public string? ProtocolVersion { get; set; }
    }
}
