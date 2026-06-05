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
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.MPesa.Configuration;
using Bhengu.Finance.Payments.MPesa.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MPesa.Providers;

/// <summary>
/// Standalone Safaricom M-Pesa <see cref="IPayoutProvider"/> implementation that wraps the
/// Daraja B2C "Business to Customer" endpoint (<c>mpesa/b2c/v3/paymentrequest</c>). Provides
/// the same OAuth token caching as <see cref="MPesaPaymentProvider"/> but exposes only the
/// payout surface so consumers can register the disbursement pipeline independently of the
/// general payment surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency:</b> M-Pesa B2C uses <c>OriginatorConversationID</c> for caller-side dedupe.
/// When <see cref="PayoutRequest.IdempotencyKey"/> is supplied it is forwarded as
/// <c>OriginatorConversationID</c> so repeated requests with the same key collapse server-side.
/// </para>
/// <para>
/// <b>RSA SecurityCredential:</b> <see cref="MPesaOptions.SecurityCredential"/> MUST be the
/// Base64 of the PKCS#1 v1.5 RSA-encrypted initiator password using the Safaricom-issued
/// public certificate (sandbox vs production certs differ — using the wrong cert silently
/// fails server-side). This adapter does not perform the encryption; the caller must precompute
/// the value and rotate it whenever the initiator password rotates.
/// </para>
/// <para>
/// <b>MSISDN format:</b> <see cref="PayoutRequest.DestinationToken"/> MUST be the recipient
/// MSISDN in international format without the leading <c>+</c> (e.g. <c>254712345678</c>).
/// Local-format numbers (<c>0712345678</c>) are silently coerced by Daraja but often produce
/// "Invalid receiver party" errors — normalise upstream.
/// </para>
/// </remarks>
public sealed class MPesaPayoutProvider : BhenguProviderBase, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly MPesaOptions _options;
    private readonly MPesaOAuthCache _tokenCache;
    private readonly string _baseUrl;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.MPesa;

    /// <summary>Construct a standalone M-Pesa payout provider with a distributed OAuth cache.</summary>
    public MPesaPayoutProvider(
        HttpClient httpClient,
        IOptions<MPesaOptions> options,
        ILogger<MPesaPayoutProvider> logger,
        MPesaOAuthCache tokenCache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.ConsumerSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.BusinessShortCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.BusinessShortCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.InitiatorName))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.InitiatorName)} is required for B2C payouts");
        if (string.IsNullOrWhiteSpace(_options.SecurityCredential))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.SecurityCredential)} is required for B2C payouts");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://sandbox.safaricom.co.ke/")
            : (_options.BaseUrl ?? "https://api.safaricom.co.ke/");

        if (!_baseUrl.EndsWith('/')) _baseUrl += "/";

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public MPesaPayoutProvider(
        HttpClient httpClient,
        IOptions<MPesaOptions> options,
        ILogger<MPesaPayoutProvider> logger)
        : this(httpClient, options, logger, new MPesaOAuthCache())
    {
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationToken))
            throw new PaymentDeclinedException(ProviderName, "invalid_msisdn",
                "M-Pesa B2C requires the recipient MSISDN in PayoutRequest.DestinationToken (e.g. 254712345678).");

        // Daraja B2C amounts must be whole-shilling integers.
        var amount = (int)Math.Round(request.Amount, MidpointRounding.AwayFromZero);
        // OriginatorConversationID is M-Pesa's idempotency knob — caller-supplied value collapses retries server-side.
        var originatorConversationId = request.IdempotencyKey ?? Guid.NewGuid().ToString();
        var commandId = request.Metadata?.TryGetValue("command_id", out var cid) == true ? cid : "BusinessPayment";

        var body = new
        {
            OriginatorConversationID = originatorConversationId,
            InitiatorName = _options.InitiatorName,
            SecurityCredential = _options.SecurityCredential,
            CommandID = commandId,
            Amount = amount,
            PartyA = _options.BusinessShortCode,
            PartyB = request.DestinationToken,
            Remarks = request.Description,
            QueueTimeOutURL = _options.QueueTimeoutUrl,
            ResultURL = _options.ResultUrl,
            Occasion = request.Description
        };

        var responseBody = await SendAsync(
            HttpMethod.Post, "mpesa/b2c/v3/paymentrequest", body, ct, "B2C").ConfigureAwait(false);

        var b2c = JsonSerializer.Deserialize<MPesaB2CResponse>(responseBody);

        Logger.LogInformation(
            "M-Pesa B2C accepted: OriginatorConversationID={OriginatorConversationId} ConversationID={ConversationId} ResponseCode={ResponseCode}",
            b2c?.OriginatorConversationID, b2c?.ConversationID, b2c?.ResponseCode);

        // ResponseCode "0" means request was accepted for processing — final outcome arrives via ResultURL callback.
        var status = b2c?.ResponseCode == "0" ? PaymentStatus.Pending : PaymentStatus.Failed;

        return new PayoutResponse
        {
            GatewayReference = b2c?.ConversationID ?? originatorConversationId,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("M-Pesa {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private Task<string> GetAccessTokenAsync(CancellationToken ct) =>
        _tokenCache.GetOrFetchAsync(_options.ConsumerKey, FetchAccessTokenAsync, ct);

    private async Task<(string AccessToken, int ExpiresInSeconds)> FetchAccessTokenAsync(CancellationToken ct)
    {
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ConsumerKey}:{_options.ConsumerSecret}"));
        using var req = new HttpRequestMessage(HttpMethod.Get, "oauth/v1/generate?grant_type=client_credentials");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("M-Pesa OAuth failed: {StatusCode} {Body}", response.StatusCode, body);
            throw new ProviderUnavailableException(ProviderName, $"M-Pesa OAuth HTTP {(int)response.StatusCode}: {body}");
        }

        var token = JsonSerializer.Deserialize<MPesaOAuthResponse>(body);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new ProviderUnavailableException(ProviderName, "M-Pesa OAuth returned an empty token");

        var expiresIn = int.TryParse(token.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 3599;
        return (token.AccessToken, expiresIn);
    }

    // ===== Daraja JSON shapes (internal) =====

    private sealed class MPesaOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public string? ExpiresIn { get; set; }
    }

    private sealed class MPesaB2CResponse
    {
        [JsonPropertyName("OriginatorConversationID")] public string? OriginatorConversationID { get; set; }
        [JsonPropertyName("ConversationID")] public string? ConversationID { get; set; }
        [JsonPropertyName("ResponseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("ResponseDescription")] public string? ResponseDescription { get; set; }
    }
}
