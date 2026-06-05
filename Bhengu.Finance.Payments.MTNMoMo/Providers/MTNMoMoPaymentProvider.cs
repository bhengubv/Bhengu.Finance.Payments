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
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.MTNMoMo.Configuration;
using Bhengu.Finance.Payments.MTNMoMo.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MTNMoMo.Providers;

/// <summary>
/// MTN Mobile Money (MoMo) provider. Implements Collection (RequestToPay) and Disbursement (Transfer).
/// <para>
/// <b>Refund note:</b> MoMo Collection has no native refund API. Reversal must be performed
/// as a Disbursement Transfer in the opposite direction. <see cref="ProcessRefundAsync"/> throws
/// <see cref="BhenguPaymentException"/> directing the caller to use <see cref="ProcessPayoutAsync"/>.
/// </para>
/// <para>
/// <b>Webhook signature note:</b> MoMo does NOT sign callbacks. Verification relies on the callback URL
/// being unguessable and on matching the inbound externalId against a known transaction.
/// </para>
/// </summary>
public sealed class MTNMoMoPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly MTNMoMoOptions _options;
    private readonly MTNMoMoOAuthCache _tokenCache;
    private readonly string _baseUrl;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.MTNMoMo;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney;

    /// <summary>Construct the provider with a distributed OAuth cache.</summary>
    public MTNMoMoPaymentProvider(
        HttpClient httpClient,
        IOptions<MTNMoMoOptions> options,
        ILogger<MTNMoMoPaymentProvider> logger,
        MTNMoMoOAuthCache tokenCache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokenCache = tokenCache ?? throw new ArgumentNullException(nameof(tokenCache));

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

    /// <summary>Back-compat constructor that uses the process-local in-memory cache.</summary>
    public MTNMoMoPaymentProvider(
        HttpClient httpClient,
        IOptions<MTNMoMoOptions> options,
        ILogger<MTNMoMoPaymentProvider> logger)
        : this(httpClient, options, logger, new MTNMoMoOAuthCache())
    {
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var msisdn = request.PaymentMethodToken;
        if (string.IsNullOrWhiteSpace(msisdn))
            throw new PaymentDeclinedException(ProviderName, "missing_msisdn",
                "MTN MoMo RequestToPay requires the payer MSISDN in PaymentRequest.PaymentMethodToken.");

        var referenceId = Guid.NewGuid().ToString();
        var externalId = request.Metadata?.TryGetValue("external_id", out var ext) == true
            ? ext
            : referenceId;

        var body = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            externalId,
            payer = new { partyIdType = "MSISDN", partyId = msisdn },
            payerMessage = request.Description,
            payeeNote = request.Description
        };

        await SendAsync(
            HttpMethod.Post, "collection/v1_0/requesttopay", body, ct, "RequestToPay",
            product: "collection", referenceId: referenceId).ConfigureAwait(false);

        Logger.LogInformation(
            "MTN MoMo RequestToPay accepted: ReferenceId={ReferenceId} ExternalId={ExternalId}",
            referenceId, externalId);

        // MoMo accepts RequestToPay with HTTP 202 — actual settlement requires status poll or callback.
        return new PaymentResponse
        {
            GatewayReference = referenceId,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = "RequestToPay accepted"
        };
    }

    /// <summary>
    /// MTN MoMo has no native refund API. Reverse a collection by issuing a new disbursement Transfer in
    /// the opposite direction via <see cref="ProcessPayoutAsync"/>.
    /// </summary>
    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync<RefundResponse>(request.GatewayReference, () =>
            throw new BhenguPaymentException(
                ProviderName,
                "MTN MoMo has no refund API. To reverse a collection, issue a Disbursement Transfer to the original payer's MSISDN via ProcessPayoutAsync."), ct);
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
                "MTN MoMo Transfer requires the payee MSISDN in PayoutRequest.DestinationToken.");

        // X-Reference-Id MUST be a hyphenated v4 UUID per MoMo spec. Honour a caller-supplied
        // IdempotencyKey when it parses as a Guid (native dedupe); otherwise mint a fresh one.
        var referenceId = !string.IsNullOrWhiteSpace(request.IdempotencyKey) && Guid.TryParse(request.IdempotencyKey, out var parsed)
            ? parsed.ToString()
            : Guid.NewGuid().ToString();
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

        await SendAsync(
            HttpMethod.Post, "disbursement/v1_0/transfer", body, ct, "Transfer",
            product: "disbursement", referenceId: referenceId).ConfigureAwait(false);

        Logger.LogInformation(
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

    /// <summary>
    /// MoMo does NOT sign webhook payloads. Verification relies on URL secrecy + matching the
    /// inbound externalId against a known transaction. Always returns false to keep callers
    /// honest; consumers should perform the externalId lookup themselves.
    /// </summary>
    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            Logger.LogWarning(
                "MTN MoMo callbacks are NOT cryptographically signed. Verify externalId against a known transaction instead.");
            // Touch SignatureHelpers.ConstantTimeEquals so the timing surface is uniform across providers
            // even though we cannot actually verify — the result is discarded.
            _ = SignatureHelpers.ConstantTimeEquals(signature, signature);
            return false;
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("webhook", () => ParseWebhookCoreAsync(payload, ct), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<MTNMoMoCallback>(payload);
            if (evt is null || string.IsNullOrEmpty(evt.ExternalId))
                return Task.FromResult<WebhookEvent?>(null);

            var status = MapStatus(evt.Status ?? string.Empty);
            Logger.LogInformation(
                "Parsed MTN MoMo callback: ExternalId={ExternalId} Status={Status}",
                evt.ExternalId, evt.Status);

            var gatewayReference = evt.FinancialTransactionId ?? evt.ExternalId;
            var eventType = (evt.Status ?? "unknown").ToLowerInvariant();
            var amount = decimal.TryParse(evt.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var a) ? a : 0m;
            var currency = (evt.Currency ?? string.Empty).ToUpperInvariant();

            // Disbursement webhooks carry a "payee" block; collection webhooks carry a "payer" block.
            // When the payload identifies a disbursement, surface a typed payout event so consumers can switch on the concrete record.
            var isDisbursement = evt.Payee is not null && !string.IsNullOrEmpty(evt.Payee.PartyId);
            if (isDisbursement)
            {
                if (status == PaymentStatus.Completed)
                {
                    return Task.FromResult<WebhookEvent?>(new PayoutCompletedEvent
                    {
                        GatewayReference = gatewayReference,
                        PayoutReference = gatewayReference,
                        Status = status,
                        EventType = eventType,
                        Category = WebhookEventCategory.PayoutCompleted,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = evt.Payee?.PartyId
                    });
                }

                if (status == PaymentStatus.Failed)
                {
                    return Task.FromResult<WebhookEvent?>(new PayoutFailedEvent
                    {
                        GatewayReference = gatewayReference,
                        PayoutReference = gatewayReference,
                        Status = status,
                        EventType = eventType,
                        Category = WebhookEventCategory.PayoutFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = evt.Reason,
                        FailureMessage = evt.Reason
                    });
                }
            }

            // Collection / fallback path — map the status family into a typed charge event where possible.
            var category = status switch
            {
                PaymentStatus.Completed => WebhookEventCategory.ChargeSucceeded,
                PaymentStatus.Pending => WebhookEventCategory.ChargePending,
                PaymentStatus.Failed => WebhookEventCategory.ChargeFailed,
                _ => WebhookEventCategory.Unknown
            };

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = gatewayReference,
                Status = status,
                EventType = eventType,
                Category = category
            });
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse MTN MoMo webhook payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // ===== HTTP plumbing =====

    private async Task SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation,
        string product, string referenceId)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var token = await GetAccessTokenAsync(product, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("X-Reference-Id", referenceId);
        req.Headers.Add("X-Target-Environment", _options.TargetEnvironment);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _options.SubscriptionKey);
        if (!string.IsNullOrWhiteSpace(_options.CallbackUrl))
            req.Headers.Add("X-Callback-Url", _options.CallbackUrl);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        // Per MoMo spec, RequestToPay/Transfer return HTTP 202 Accepted on success with empty body.
        if (response.IsSuccessStatusCode) return;

        Logger.LogError("MTN MoMo {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
        if ((int)response.StatusCode is >= 400 and < 500)
            throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
        throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
    }

    private Task<string> GetAccessTokenAsync(string product, CancellationToken ct) =>
        _tokenCache.GetOrFetchAsync(product, _options.ApiUserId, ct2 => FetchAccessTokenAsync(product, ct2), ct);

    private async Task<(string AccessToken, int ExpiresInSeconds)> FetchAccessTokenAsync(string product, CancellationToken ct)
    {
        var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ApiUserId}:{_options.ApiKey}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{product}/token/");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
        req.Headers.Add("Ocp-Apim-Subscription-Key", _options.SubscriptionKey);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("MTN MoMo {Product} OAuth failed: {StatusCode} {Body}", product, response.StatusCode, body);
            throw new ProviderUnavailableException(ProviderName, $"MTN MoMo {product} OAuth HTTP {(int)response.StatusCode}: {body}");
        }

        var token = JsonSerializer.Deserialize<MTNMoMoOAuthResponse>(body);
        if (token is null || string.IsNullOrEmpty(token.AccessToken))
            throw new ProviderUnavailableException(ProviderName, $"MTN MoMo {product} OAuth returned an empty token");

        return (token.AccessToken, token.ExpiresIn > 0 ? token.ExpiresIn : 3599);
    }

    private static PaymentStatus MapStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "SUCCESSFUL" or "SUCCESS" or "COMPLETED" => PaymentStatus.Completed,
        "PENDING" or "ONGOING" => PaymentStatus.Pending,
        "FAILED" or "REJECTED" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" or "TIMEOUT" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // ===== MoMo JSON shapes (internal) =====

    private sealed class MTNMoMoOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class MTNMoMoCallback
    {
        [JsonPropertyName("financialTransactionId")] public string? FinancialTransactionId { get; set; }
        [JsonPropertyName("externalId")] public string? ExternalId { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("payee")] public MTNMoMoParty? Payee { get; set; }
        [JsonPropertyName("payer")] public MTNMoMoParty? Payer { get; set; }
    }

    private sealed class MTNMoMoParty
    {
        [JsonPropertyName("partyIdType")] public string? PartyIdType { get; set; }
        [JsonPropertyName("partyId")] public string? PartyId { get; set; }
    }
}
