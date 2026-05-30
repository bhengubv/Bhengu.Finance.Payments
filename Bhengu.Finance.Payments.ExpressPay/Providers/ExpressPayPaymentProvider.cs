// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.ExpressPay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ExpressPay.Providers;

/// <summary>
/// ExpressPay (Ghana, Gambia, Sierra Leone, Liberia, Nigeria) payment gateway provider.
/// Wraps the form-encoded submit.php / query.php API. ExpressPay does NOT issue HMAC on
/// the post-url callback; <see cref="VerifyWebhookSignature"/> performs a constant-time
/// equality check between the supplied signature and the configured ApiKey, requiring
/// the caller to forward the api-key in a trusted reverse-proxy header. Refund and payout
/// are not exposed by the standard API.
/// </summary>
public sealed class ExpressPayPaymentProvider : IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly ExpressPayOptions _options;
    private readonly ILogger<ExpressPayPaymentProvider> _logger;

    public string ProviderName => ProviderNames.ExpressPay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Cards |
        ProviderCapabilities.MobileMoney;

    public ExpressPayPaymentProvider(
        HttpClient httpClient,
        IOptions<ExpressPayOptions> options,
        ILogger<ExpressPayPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ExpressPayOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var raw = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://sandbox.expresspaygh.com/api/"
                : _options.BaseUrl ?? "https://expresspay.com.gh/api/";
            if (!raw.EndsWith('/')) raw += "/";
            _httpClient.BaseAddress = new Uri(raw);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var form = new Dictionary<string, string>
        {
            ["merchant-id"] = _options.MerchantId,
            ["api-key"] = _options.ApiKey,
            ["currency"] = request.Currency.ToUpperInvariant(),
            ["amount"] = request.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["order-id"] = request.PaymentMethodToken,
            ["order-desc"] = request.Description,
            ["redirect-url"] = _options.RedirectUrl,
            ["post-url"] = _options.PostUrl,
            ["accountnumber"] = request.Metadata?.GetValueOrDefault("accountnumber") ?? "",
            ["username"] = request.Metadata?.GetValueOrDefault("username") ?? "",
            ["email"] = request.Metadata?.GetValueOrDefault("email") ?? "",
            ["firstname"] = request.Metadata?.GetValueOrDefault("firstname") ?? "",
            ["lastname"] = request.Metadata?.GetValueOrDefault("lastname") ?? ""
        };

        var responseBody = await SendFormAsync(HttpMethod.Post, "submit.php", form, ct, "ProcessPayment").ConfigureAwait(false);
        var submit = JsonSerializer.Deserialize<ExpressPaySubmitResponse>(responseBody);

        _logger.LogInformation("ExpressPay submit: status={Status} token={Token} url={Url}",
            submit?.Status, submit?.Token, submit?.PaymentUrl);

        return new PaymentResponse
        {
            GatewayReference = submit?.Token ?? request.PaymentMethodToken,
            Status = submit?.Status == 1 ? PaymentStatus.Pending : PaymentStatus.Failed,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = submit?.PaymentUrl,
            Message = submit?.Message
        };
    }

    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        throw new BhenguPaymentException(
            ProviderName,
            "ExpressPay does not expose a refund API — issue refunds via the ExpressPay merchant portal.",
            providerErrorCode: "not_supported");
    }

    /// <summary>
    /// Query the status of an ExpressPay token via query.php. Returns the raw API response body.
    /// </summary>
    public async Task<string> QueryStatusAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var form = new Dictionary<string, string>
        {
            ["merchant-id"] = _options.MerchantId,
            ["api-key"] = _options.ApiKey,
            ["token"] = token
        };
        return await SendFormAsync(HttpMethod.Post, "query.php", form, ct, "QueryStatus").ConfigureAwait(false);
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("ExpressPay ApiKey not configured — webhook verification cannot succeed.");
            return false;
        }

        // ExpressPay does NOT HMAC its post-url callbacks. Constant-time compare the supplied
        // signature (which the caller must source from a trusted reverse-proxy header) with the
        // configured ApiKey. Production callers SHOULD additionally call QueryStatusAsync(token).
        var a = Encoding.UTF8.GetBytes(signature);
        var b = Encoding.UTF8.GetBytes(_options.ApiKey);
        if (a.Length != b.Length) return false;
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            ExpressPayCallback? cb;
            if (payload.TrimStart().StartsWith('{'))
            {
                cb = JsonSerializer.Deserialize<ExpressPayCallback>(payload);
            }
            else
            {
                var bag = ParseForm(payload);
                int.TryParse(bag.GetValueOrDefault("status"), out var sint);
                cb = new ExpressPayCallback
                {
                    Token = bag.GetValueOrDefault("token"),
                    Status = sint,
                    Currency = bag.GetValueOrDefault("currency"),
                    Amount = bag.GetValueOrDefault("amount")
                };
            }

            if (cb is null || string.IsNullOrEmpty(cb.Token))
                return Task.FromResult<WebhookEvent?>(null);

            var status = cb.Status switch
            {
                1 => PaymentStatus.Completed,
                2 => PaymentStatus.Pending,
                3 => PaymentStatus.Failed,
                4 => PaymentStatus.Cancelled,
                _ => (PaymentStatus?)null
            };

            if (status is null) return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = cb.Token!,
                Status = status.Value,
                EventType = cb.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse ExpressPay callback");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendFormAsync(HttpMethod method, string path, Dictionary<string, string> form, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new FormUrlEncodedContent(form)
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to ExpressPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("ExpressPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static Dictionary<string, string> ParseForm(string raw)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(raw)) return dict;
        foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            dict[WebUtility.UrlDecode(pair[..eq])] = WebUtility.UrlDecode(pair[(eq + 1)..]);
        }
        return dict;
    }

    private sealed class ExpressPaySubmitResponse
    {
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("payment_url")] public string? PaymentUrl { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class ExpressPayCallback
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("amount")] public string? Amount { get; set; }
    }
}
