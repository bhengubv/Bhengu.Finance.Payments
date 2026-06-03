// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MTNMoMo.Providers;

/// <summary>
/// Standalone MTN MoMo Disbursement <see cref="IPayoutProvider"/> implementation that wraps the
/// MoMo Disbursements <c>v1_0/transfer</c> endpoint. Distinct from <see cref="MTNMoMoPaymentProvider"/>'s
/// own <see cref="IPayoutProvider"/> surface so consumers can register the disbursement pipeline
/// independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency:</b> <see cref="PayoutRequest.IdempotencyKey"/> is forwarded as the
/// <c>X-Reference-Id</c> header — MoMo's native idempotency knob. Retries with the same UUID
/// collapse to the same transfer server-side. The same UUID also becomes <c>externalId</c> in
/// the body so the inbound webhook can be correlated back to the caller's logical id. When no
/// key is supplied, a new UUID is generated per request.
/// </para>
/// <para>
/// <b>X-Reference-Id format:</b> MoMo REQUIRES the header to be a hyphenated v4 UUID. Pass a
/// random GUID — string-encoded business ids will be rejected with 400 Bad Request.
/// </para>
/// <para>
/// <b>MSISDN format:</b> <see cref="PayoutRequest.DestinationToken"/> MUST be the payee MSISDN
/// in international format without the leading <c>+</c> (e.g. <c>256776123456</c>). MoMo's
/// validator returns "PAYEE_NOT_FOUND" for unrecognised formats.
/// </para>
/// <para>
/// <b>OAuth token caching:</b> the disbursement product token is exchanged on demand and cached
/// in-process until ~1 minute before expiry. Tokens are NOT shared with the collection product
/// — disbursement and collection have distinct API users.
/// </para>
/// </remarks>
public sealed class MTNMoMoPayoutProvider : IPayoutProvider
{
    private const string ProductName = "disbursement";

    private readonly HttpClient _httpClient;
    private readonly MTNMoMoOptions _options;
    private readonly ILogger<MTNMoMoPayoutProvider> _logger;
    private readonly string _baseUrl;

    private string? _cachedToken;
    private DateTime _cachedTokenExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.MTNMoMo;

    /// <summary>Construct a standalone MTN MoMo payout provider. Designed to be registered via DI.</summary>
    public MTNMoMoPayoutProvider(
        HttpClient httpClient,
        IOptions<MTNMoMoOptions> options,
        ILogger<MTNMoMoPayoutProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SubscriptionKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MTNMoMoOptions.SubscriptionKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiUserId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MTNMoMoOptions.ApiUserId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MTNMoMoOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.TargetEnvironment))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MTNMoMoOptions.TargetEnvironment)} is required");

        _baseUrl = _options.BaseUrl ?? (_options.UseSandbox
            ? "https://sandbox.momodeveloper.mtn.com/"
            : "https://momodeveloper.mtn.com/");
        if (!_baseUrl.EndsWith('/')) _baseUrl += "/";

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    /// <inheritdoc/>
    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DestinationToken))
            throw new PaymentDeclinedException(ProviderName, "invalid_msisdn",
                "MTN MoMo Transfer requires the payee MSISDN in PayoutRequest.DestinationToken.");

        // X-Reference-Id MUST be a hyphenated v4 UUID per MoMo spec. If a caller-supplied
        // IdempotencyKey is already a valid UUID we honour it (native dedupe); otherwise mint one.
        var referenceId = !string.IsNullOrWhiteSpace(request.IdempotencyKey) && Guid.TryParse(request.IdempotencyKey, out var parsed)
            ? parsed.ToString()
            : Guid.NewGuid().ToString();

        // externalId surfaces in the webhook so the caller can correlate. Bind it to the reference id by default.
        var externalId = request.IdempotencyKey ?? referenceId;

        var body = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            externalId,
            payee = new { partyIdType = "MSISDN", partyId = request.DestinationToken },
            payerMessage = request.Description,
            payeeNote = request.Description
        };

        await SendAsync(HttpMethod.Post, "disbursement/v1_0/transfer", body, ct, "Transfer", referenceId).ConfigureAwait(false);

        _logger.LogInformation(
            "MTN MoMo Disbursement Transfer accepted: ReferenceId={ReferenceId} ExternalId={ExternalId}",
            referenceId, externalId);

        return new PayoutResponse
        {
            GatewayReference = referenceId,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    private async Task SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation, string referenceId)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Reference-Id", referenceId);
        req.Headers.Add("X-Target-Environment", _options.TargetEnvironment);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _options.SubscriptionKey);
        if (!string.IsNullOrWhiteSpace(_options.CallbackUrl))
            req.Headers.Add("X-Callback-Url", _options.CallbackUrl);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, $"HTTP request to MTN MoMo ({operation}) failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        // Per MoMo spec, Transfer returns HTTP 202 Accepted on success with empty body.
        if (response.IsSuccessStatusCode) return;

        _logger.LogError("MTN MoMo {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
        if ((int)response.StatusCode is >= 400 and < 500)
            throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
        throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            return _cachedToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
                return _cachedToken;

            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ApiUserId}:{_options.ApiKey}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{ProductName}/token/");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
            req.Headers.Add("Ocp-Apim-Subscription-Key", _options.SubscriptionKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "MTN MoMo disbursement OAuth call failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("MTN MoMo disbursement OAuth failed: {StatusCode} {Body}", response.StatusCode, body);
                throw new ProviderUnavailableException(ProviderName, $"MTN MoMo disbursement OAuth HTTP {(int)response.StatusCode}: {body}");
            }

            var token = JsonSerializer.Deserialize<MTNMoMoOAuthResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "MTN MoMo disbursement OAuth returned an empty token");

            _cachedToken = token.AccessToken;
            _cachedTokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3599);
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed class MTNMoMoOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
