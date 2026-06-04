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
using Bhengu.Finance.Payments.Core.Observability;
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
public sealed class YocoThreeDSecureProvider : IThreeDSecureProvider
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;
    private readonly ILogger<YocoThreeDSecureProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Yoco;

    /// <summary>Create a new Yoco 3DS provider bound to the supplied HTTP client and options.</summary>
    public YocoThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoThreeDSecureProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc />
    public async Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "three_d_secure.start");
        try
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
            var response = JsonSerializer.Deserialize<YocoChargeWithAuthResponse>(raw);

            _logger.LogInformation("Yoco 3DS challenge: chargeId={Id} status={Status} requiresChallenge={Requires}",
                response?.Id, response?.Status, response?.NextAction?.RedirectUrl is not null);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

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
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "three_d_secure.get");
        try
        {
            var raw = await SendAsync(HttpMethod.Get, $"charges/{Uri.EscapeDataString(challengeReference)}", body: null, ct, "GetChallenge", idempotencyKey: null).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<YocoChargeWithAuthResponse>(raw);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

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
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation, string? idempotencyKey)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Yoco failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class YocoChargeWithAuthResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("nextAction")] public YocoNextAction? NextAction { get; set; }
        [JsonPropertyName("threeDSecure")] public YocoThreeDSecure? ThreeDSecure { get; set; }
    }

    private sealed class YocoNextAction
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("redirectUrl")] public string? RedirectUrl { get; set; }
    }

    private sealed class YocoThreeDSecure
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("dsTransactionId")] public string? DsTransactionId { get; set; }
        [JsonPropertyName("eci")] public string? Eci { get; set; }
    }
}
