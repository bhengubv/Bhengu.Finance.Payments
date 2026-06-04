// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco Online Payments 3-D Secure provider. Wraps Yoco's <c>POST /v1/charges</c> endpoint
/// with the <c>liabilityShift=true</c> + <c>threeDSecure=true</c> request fields that force
/// the issuer challenge.
/// </summary>
/// <remarks>
/// Yoco's response includes a <c>nextAction.redirectUrl</c> when a challenge is required, which
/// the merchant must surface to the payer's browser. Once the payer completes the challenge
/// Yoco's webhook (<c>payment.succeeded</c> / <c>payment.failed</c>) fires with the final state.
/// </remarks>
public sealed class YocoThreeDSecureProvider : BhenguProviderBase, IThreeDSecureProvider
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Yoco;

    /// <summary>Create a new Yoco 3DS provider bound to the supplied HTTP client and options.</summary>
    public YocoThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoThreeDSecureProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc />
    public Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);
        return RunOperationAsync("three_d_secure.start", () => StartAuthenticationCoreAsync(chargeIntent, ct), ct);
    }

    private async Task<ThreeDSecureChallenge> StartAuthenticationCoreAsync(PaymentRequest chargeIntent, CancellationToken ct)
    {
        var amountInCents = (int)(chargeIntent.Amount * 100);
        var body = new
        {
            token = chargeIntent.PaymentMethodToken,
            amountInCents,
            currency = chargeIntent.Currency.ToUpperInvariant(),
            threeDSecure = true,
            liabilityShift = true,
            metadata = chargeIntent.Metadata
        };

        var raw = await SendAsync(HttpMethod.Post, "charges/", body, ct, "StartAuthentication", chargeIntent.IdempotencyKey).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<YocoChargeWithAuthResponse>(raw, s_jsonOptions);

        Logger.LogInformation("Yoco 3DS challenge: chargeId={Id} status={Status} requiresChallenge={Requires}",
            response?.Id, response?.Status, response?.NextAction?.RedirectUrl is not null);

        var status = response?.Status?.ToLowerInvariant() switch
        {
            "successful" or "succeeded" or "authenticated" => ThreeDSecureStatus.Authenticated,
            "pending" or "processing" or "requires_action" => ThreeDSecureStatus.ChallengeRequired,
            "failed" or "declined" => ThreeDSecureStatus.Failed,
            _ => response?.NextAction?.RedirectUrl is not null
                ? ThreeDSecureStatus.ChallengeRequired
                : ThreeDSecureStatus.NotRequired
        };

        return new ThreeDSecureChallenge
        {
            Status = status,
            ChallengeReference = response?.Id ?? string.Empty,
            RedirectUrl = response?.NextAction?.RedirectUrl,
            ProtocolVersion = response?.ThreeDSecure?.Version ?? "2.2.0",
            DsTransactionId = response?.ThreeDSecure?.DsTransactionId
        };
    }

    /// <inheritdoc />
    public Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeReference);
        return RunOperationAsync("three_d_secure.get", () => GetChallengeCoreAsync(challengeReference, ct), ct);
    }

    private async Task<ThreeDSecureChallenge> GetChallengeCoreAsync(string challengeReference, CancellationToken ct)
    {
        var raw = await SendAsync(HttpMethod.Get, $"charges/{Uri.EscapeDataString(challengeReference)}", body: null, ct, "GetChallenge", idempotencyKey: null).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<YocoChargeWithAuthResponse>(raw, s_jsonOptions);

        var status = response?.Status?.ToLowerInvariant() switch
        {
            "successful" or "succeeded" or "authenticated" => ThreeDSecureStatus.Authenticated,
            "pending" or "processing" or "requires_action" => ThreeDSecureStatus.ChallengeRequired,
            "failed" or "declined" => ThreeDSecureStatus.Failed,
            _ => ThreeDSecureStatus.Failed
        };

        return new ThreeDSecureChallenge
        {
            Status = status,
            ChallengeReference = challengeReference,
            ProtocolVersion = response?.ThreeDSecure?.Version ?? "2.2.0",
            DsTransactionId = response?.ThreeDSecure?.DsTransactionId
        };
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation, string? idempotencyKey)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, s_jsonOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class YocoChargeWithAuthResponse
    {
        public string? Id { get; set; }
        public string? Status { get; set; }
        public YocoNextAction? NextAction { get; set; }
        public YocoThreeDSecure? ThreeDSecure { get; set; }
    }

    private sealed class YocoNextAction
    {
        public string? Type { get; set; }
        public string? RedirectUrl { get; set; }
    }

    private sealed class YocoThreeDSecure
    {
        public string? Version { get; set; }
        public string? DsTransactionId { get; set; }
        public string? Eci { get; set; }
    }
}
