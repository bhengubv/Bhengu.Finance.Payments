// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.AirtelMoney.Configuration;
using Bhengu.Finance.Payments.AirtelMoney.Internals;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.AirtelMoney.Providers;

/// <summary>
/// Standalone Airtel Money Disbursement <see cref="IPayoutProvider"/> implementation that wraps
/// the Airtel Africa Open API <c>standard/v1/disbursements/</c> endpoint. Distinct from
/// <see cref="AirtelMoneyPaymentProvider"/>'s own <see cref="IPayoutProvider"/> surface so
/// consumers can register the disbursement pipeline independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency:</b> <see cref="PayoutRequest.IdempotencyKey"/> is forwarded as the disbursement
/// <c>transaction.id</c> field — Airtel uses this for caller-side dedupe within a window. The
/// same value is also written to the <c>reference</c> field so it surfaces on the webhook.
/// When no key is supplied, a 16-char hex id is minted per request.
/// </para>
/// <para>
/// <b>PIN encryption:</b> Production Airtel Disbursement requests must include a <c>pin</c> field
/// that is the merchant initiator's PIN encrypted with Airtel's RSA public key (Base64). This
/// adapter forwards <see cref="AirtelMoneyOptions.EncryptedDisbursementPin"/> verbatim — the
/// caller MUST precompute the ciphertext out-of-band. UAT/sandbox allows the empty PIN; do NOT
/// rely on that behaviour in production.
/// </para>
/// <para>
/// <b>MSISDN format:</b> <see cref="PayoutRequest.DestinationToken"/> MUST be the recipient
/// MSISDN in international format without the leading <c>+</c> (e.g. <c>254712345678</c>).
/// Local-format numbers are rejected with status "DP00800001006".
/// </para>
/// </remarks>
public sealed class AirtelMoneyPayoutProvider : BhenguProviderBase, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly AirtelMoneyOptions _options;
    private readonly AirtelMoneyOAuthCache _tokenCache;
    private readonly string _baseUrl;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.AirtelMoney;

    /// <summary>Construct a standalone Airtel Money payout provider with a distributed OAuth cache.</summary>
    public AirtelMoneyPayoutProvider(
        HttpClient httpClient,
        IOptions<AirtelMoneyOptions> options,
        ILogger<AirtelMoneyPayoutProvider> logger,
        AirtelMoneyOAuthCache tokenCache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.ClientSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.Country))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.Country)} is required");
        if (string.IsNullOrWhiteSpace(_options.Currency))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(AirtelMoneyOptions.Currency)} is required");

        _baseUrl = _options.BaseUrl ?? (_options.UseSandbox
            ? "https://openapiuat.airtel.africa/"
            : "https://openapi.airtel.africa/");
        if (!_baseUrl.EndsWith('/')) _baseUrl += "/";

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public AirtelMoneyPayoutProvider(
        HttpClient httpClient,
        IOptions<AirtelMoneyOptions> options,
        ILogger<AirtelMoneyPayoutProvider> logger)
        : this(httpClient, options, logger, new AirtelMoneyOAuthCache())
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
                "Airtel Money Disbursement requires the recipient MSISDN in PayoutRequest.DestinationToken.");

        var transactionId = !string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? request.IdempotencyKey!
            : Guid.NewGuid().ToString("N")[..16];

        var body = new
        {
            payee = new { msisdn = request.DestinationToken },
            reference = transactionId,
            pin = _options.EncryptedDisbursementPin ?? string.Empty,
            transaction = new
            {
                amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
                id = transactionId
            }
        };

        var responseBody = await SendAsync(
            HttpMethod.Post, "standard/v1/disbursements/", body, ct, "Disbursement").ConfigureAwait(false);

        var result = JsonSerializer.Deserialize<AirtelEnvelope<AirtelDisbursementData>>(responseBody);
        var status = MapStatus(result?.Data?.Transaction?.Status);

        Logger.LogInformation(
            "Airtel Money Disbursement accepted: TransactionId={TransactionId} Status={Status}",
            transactionId, result?.Data?.Transaction?.Status);

        return new PayoutResponse
        {
            GatewayReference = result?.Data?.Transaction?.Id ?? transactionId,
            Status = status,
            Amount = request.Amount,
            Currency = _options.Currency,
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
        req.Headers.Add("X-Country", _options.Country);
        req.Headers.Add("X-Currency", _options.Currency);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Airtel Money {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private Task<string> GetAccessTokenAsync(CancellationToken ct) =>
        _tokenCache.GetOrFetchAsync(_options.ClientId, FetchAccessTokenAsync, ct);

    private async Task<(string AccessToken, int ExpiresInSeconds)> FetchAccessTokenAsync(CancellationToken ct)
    {
        var body = new
        {
            client_id = _options.ClientId,
            client_secret = _options.ClientSecret,
            grant_type = "client_credentials"
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "auth/oauth2/token")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Airtel Money OAuth failed: {StatusCode} {Body}", response.StatusCode, responseBody);
            throw new ProviderUnavailableException(ProviderName, $"Airtel Money OAuth HTTP {(int)response.StatusCode}: {responseBody}");
        }

        var token = JsonSerializer.Deserialize<AirtelOAuthResponse>(responseBody);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new ProviderUnavailableException(ProviderName, "Airtel Money OAuth returned an empty token");

        return (token.AccessToken, token.ExpiresIn > 0 ? token.ExpiresIn : 3599);
    }

    private static PaymentStatus MapStatus(string? raw) => (raw ?? string.Empty).ToUpperInvariant() switch
    {
        "TS" or "SUCCESS" or "SUCCESSFUL" or "COMPLETED" => PaymentStatus.Completed,
        "TIP" or "PENDING" or "IN_PROGRESS" => PaymentStatus.Pending,
        "TF" or "FAILED" or "DECLINED" => PaymentStatus.Failed,
        "TA" or "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // ===== Airtel JSON shapes (internal) =====

    private sealed class AirtelOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class AirtelEnvelope<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
        [JsonPropertyName("status")] public AirtelStatus? Status { get; set; }
    }

    private sealed class AirtelStatus
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("success")] public bool Success { get; set; }
    }

    private sealed class AirtelDisbursementData
    {
        [JsonPropertyName("transaction")] public AirtelTransaction? Transaction { get; set; }
    }

    private sealed class AirtelTransaction
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("airtel_money_id")] public string? AirtelMoneyId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
