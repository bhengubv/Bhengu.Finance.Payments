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
using Bhengu.Finance.Payments.OrangeMoney.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.OrangeMoney.Providers;

/// <summary>
/// Orange Money Web Payment provider. Checkout-redirect flow: the SDK creates a payment session and
/// returns the hosted payment URL in <see cref="PaymentResponse.RedirectUrl"/>; the caller redirects the payer
/// to that URL.
/// <para>
/// <b>Refund note:</b> The Orange Money Web Payment API has no automated refund endpoint.
/// <see cref="ProcessRefundAsync"/> throws <see cref="BhenguPaymentException"/> directing the caller to
/// process the reversal manually via the Orange Money merchant portal.
/// </para>
/// <para>
/// <b>Payouts:</b> Orange Money Web Payment does not expose disbursements, so this provider
/// intentionally does NOT implement <see cref="IPayoutProvider"/>.
/// </para>
/// <para>
/// <b>Webhook signature note:</b> Orange Money's notif callback does not carry a cryptographic signature.
/// Verification is performed by calling the <c>transactionstatus</c> endpoint with the <c>notif_token</c>
/// received in the payment-creation response. <see cref="VerifyWebhookSignature"/> compares the supplied
/// signature against the cached notif_token; callers must persist that token alongside their order record.
/// </para>
/// </summary>
public sealed class OrangeMoneyPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly OrangeMoneyOptions _options;
    private readonly ILogger<OrangeMoneyPaymentProvider> _logger;
    private readonly string _baseUrl;

    private string? _cachedAccessToken;
    private DateTime _cachedTokenExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public string ProviderName => ProviderNames.OrangeMoney;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.MobileMoney;

    public OrangeMoneyPaymentProvider(
        HttpClient httpClient,
        IOptions<OrangeMoneyOptions> options,
        ILogger<OrangeMoneyPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.ConsumerSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Country))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OrangeMoneyOptions.Country)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://api.orange.com/")
            : (_options.BaseUrl ?? "https://api.orange.com/");
        if (!_baseUrl.EndsWith('/')) _baseUrl += "/";

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var orderId = request.Metadata?.TryGetValue("order_id", out var oid) == true
            ? oid
            : Guid.NewGuid().ToString("N")[..20];
        var lang = request.Metadata?.TryGetValue("lang", out var l) == true ? l : "fr";

        var body = new
        {
            merchant_key = _options.MerchantKey,
            currency = request.Currency.ToUpperInvariant(),
            order_id = orderId,
            amount = (int)Math.Round(request.Amount, MidpointRounding.AwayFromZero),
            return_url = _options.ReturnUrl,
            cancel_url = _options.CancelUrl,
            notif_url = _options.NotifUrl,
            lang,
            reference = request.Description
        };

        var (responseBody, _) = await SendAsync(
            HttpMethod.Post, $"orange-money-webpay/{_options.Country}/v1/webpayment", body, ct, "Webpayment").ConfigureAwait(false);

        var wp = JsonSerializer.Deserialize<OrangeWebPaymentResponse>(responseBody);

        _logger.LogInformation(
            "Orange Money web payment created: OrderId={OrderId} PayToken={PayToken} Status={Status}",
            orderId, wp?.PayToken, wp?.Status);

        // payment_url is returned to the caller via RedirectUrl — they redirect the payer to it.
        return new PaymentResponse
        {
            GatewayReference = wp?.PayToken ?? orderId,
            Status = MapStatus(wp?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = wp?.PaymentUrl,
            Message = wp?.Message ?? wp?.Status
        };
    }

    /// <summary>
    /// Orange Money Web Payment has no automated refund endpoint. Process the reversal manually via the
    /// Orange Money merchant portal.
    /// </summary>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "Orange Money Web Payment has no automated refund API. Reverse the transaction via the Orange Money merchant portal.");
    }

    /// <summary>
    /// Orange Money does NOT cryptographically sign callbacks. Pass the persisted <c>notif_token</c>
    /// (returned by <see cref="ProcessPaymentAsync"/> as part of the underlying API response) as
    /// <paramref name="signature"/>. The payload's <c>notif_token</c> field is compared in constant time.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        try
        {
            var evt = JsonSerializer.Deserialize<OrangeNotifPayload>(payload);
            var notifToken = evt?.NotifToken;
            if (string.IsNullOrEmpty(notifToken))
            {
                _logger.LogWarning("Orange Money notif payload missing notif_token — cannot verify.");
                return false;
            }

            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(notifToken),
                Encoding.UTF8.GetBytes(signature));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orange Money notif token verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var evt = JsonSerializer.Deserialize<OrangeNotifPayload>(payload);
            if (evt is null || string.IsNullOrEmpty(evt.PayToken))
                return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation(
                "Parsed Orange Money notif: PayToken={PayToken} Status={Status}",
                evt.PayToken, evt.Status);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.PayToken,
                Status = MapStatus(evt.Status),
                EventType = (evt.Status ?? "unknown").ToLowerInvariant()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse Orange Money notif payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // ===== HTTP plumbing =====

    private async Task<(string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, $"HTTP request to Orange Money ({operation}) failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Orange Money {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return (responseBody, response);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedAccessToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            return _cachedAccessToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedAccessToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
                return _cachedAccessToken;

            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ConsumerKey}:{_options.ConsumerSecret}"));
            using var req = new HttpRequestMessage(HttpMethod.Post, "oauth/v2/token")
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "client_credentials")
                })
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "Orange Money OAuth call failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Orange Money OAuth failed: {StatusCode} {Body}", response.StatusCode, body);
                throw new ProviderUnavailableException(ProviderName, $"Orange Money OAuth HTTP {(int)response.StatusCode}: {body}");
            }

            var token = JsonSerializer.Deserialize<OrangeOAuthResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "Orange Money OAuth returned an empty token");

            _cachedAccessToken = token.AccessToken;
            _cachedTokenExpiresUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3599);
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static PaymentStatus MapStatus(string? raw) => (raw ?? string.Empty).ToUpperInvariant() switch
    {
        "SUCCESS" or "SUCCESSFUL" or "COMPLETED" or "PAID" => PaymentStatus.Completed,
        "INITIATED" or "PENDING" or "PROCESSING" => PaymentStatus.Pending,
        "FAILED" or "DECLINED" or "EXPIRED" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // ===== Orange JSON shapes (internal) =====

    private sealed class OrangeOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private sealed class OrangeWebPaymentResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("pay_token")] public string? PayToken { get; set; }
        [JsonPropertyName("payment_url")] public string? PaymentUrl { get; set; }
        [JsonPropertyName("notif_token")] public string? NotifToken { get; set; }
    }

    private sealed class OrangeNotifPayload
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("pay_token")] public string? PayToken { get; set; }
        [JsonPropertyName("order_id")] public string? OrderId { get; set; }
        [JsonPropertyName("notif_token")] public string? NotifToken { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("txnid")] public string? TxnId { get; set; }
    }
}
