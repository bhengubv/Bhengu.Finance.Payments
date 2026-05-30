// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.TymeBank.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.TymeBank.Providers;

/// <summary>
/// TymeBank (South Africa) pay-by-bank, Scan-to-Pay QR, and PayShap/EFT payout provider.
/// <see cref="IPaymentGatewayProvider.ProcessPaymentAsync"/> initiates an instant pay-by-bank transfer
/// (when <c>request.Metadata["mode"]="qr"</c> a QR is generated instead). Refund and payout (PayShap or EFT)
/// are supported via dedicated endpoints. Webhook authenticity uses HMAC-SHA256 in <c>X-Tyme-Signature</c>.
/// </summary>
public sealed class TymeBankPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly TymeBankOptions _options;
    private readonly ILogger<TymeBankPaymentProvider> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;

    public string ProviderName => "tymebank";

    public TymeBankPaymentProvider(
        HttpClient httpClient,
        IOptions<TymeBankOptions> options,
        ILogger<TymeBankPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(TymeBankOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(TymeBankOptions.ClientSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-sandbox.tymebank.co.za"
                : _options.BaseUrl ?? "https://api.tymebank.co.za";
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mode-switch: "qr" generates a Scan-to-Pay QR; otherwise initiate a pay-by-bank transfer.
        var mode = request.Metadata?.GetValueOrDefault("mode")?.ToLowerInvariant() ?? "instant";

        if (mode == "qr")
            return await ProcessQrAsync(request, ct).ConfigureAwait(false);

        var debtorAccount = request.Metadata?.GetValueOrDefault("debtor_account") ?? string.Empty;
        var debtorBranch = request.Metadata?.GetValueOrDefault("debtor_branch_code") ?? string.Empty;
        var creditorAccount = request.Metadata?.GetValueOrDefault("creditor_account") ?? string.Empty;
        var creditorBranch = request.Metadata?.GetValueOrDefault("creditor_branch_code") ?? string.Empty;
        var creditorName = request.Metadata?.GetValueOrDefault("creditor_name") ?? request.Description;

        var requestBody = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            debtor = new { account_number = debtorAccount, branch_code = debtorBranch },
            creditor = new { account_number = creditorAccount, branch_code = creditorBranch, name = creditorName },
            reference = request.PaymentMethodToken,
            narration = request.Description
        };

        var body = await SendAsync(HttpMethod.Post, "v1/payments/instant", requestBody, ct, "ProcessPayment")
            .ConfigureAwait(false);
        var paymentResponse = JsonSerializer.Deserialize<TymeBankPaymentResponse>(body);

        _logger.LogInformation("TymeBank instant payment: id={Id} status={Status}",
            paymentResponse?.PaymentId, paymentResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = paymentResponse?.PaymentId ?? string.Empty,
            Status = MapStatus(paymentResponse?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = paymentResponse?.Status
        };
    }

    private async Task<PaymentResponse> ProcessQrAsync(PaymentRequest request, CancellationToken ct)
    {
        var expiry = 10;
        if (request.Metadata?.TryGetValue("expiry_minutes", out var expiryStr) == true &&
            int.TryParse(expiryStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            expiry = parsed;
        }

        var requestBody = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            merchant_ref = request.PaymentMethodToken,
            expiry_minutes = expiry
        };

        var body = await SendAsync(HttpMethod.Post, "v1/qr/generate", requestBody, ct, "ProcessPaymentQr")
            .ConfigureAwait(false);
        var qrResponse = JsonSerializer.Deserialize<TymeBankQrResponse>(body);

        _logger.LogInformation("TymeBank QR generated: qrId={QrId}", qrResponse?.QrId);

        return new PaymentResponse
        {
            GatewayReference = qrResponse?.QrId ?? request.PaymentMethodToken,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = qrResponse?.QrString ?? qrResponse?.QrImageUrl
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestBody = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            reason = request.Reason
        };

        var path = $"v1/payments/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<TymeBankRefundResponse>(body);

        _logger.LogInformation("TymeBank refund: refundId={RefundId} for payment {PaymentId}",
            refundResponse?.RefundId, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.RefundId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refundResponse?.Status),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken format: "<bankCode>:<accountNumber>:<beneficiaryName>"
        var parts = request.DestinationToken.Split(':');
        if (parts.Length < 2)
            throw new BhenguPaymentException(ProviderName,
                "TymeBank PayoutRequest.DestinationToken must be 'bankCode:accountNumber[:beneficiaryName]'.",
                providerErrorCode: "invalid_destination");

        var beneficiaryName = parts.Length > 2 ? parts[2] : request.Description;
        var reference = $"tyme-payout-{Guid.NewGuid():N}";

        var requestBody = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            beneficiary_account = parts[1],
            beneficiary_bank_code = parts[0],
            beneficiary_name = beneficiaryName,
            reference
        };

        var body = await SendAsync(HttpMethod.Post, "v1/payouts", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var payoutResponse = JsonSerializer.Deserialize<TymeBankPayoutResponse>(body);

        _logger.LogInformation("TymeBank payout queued: payoutId={PayoutId} status={Status}",
            payoutResponse?.PayoutId, payoutResponse?.Status);

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.PayoutId ?? reference,
            Status = MapStatus(payoutResponse?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("TymeBank WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            var supplied = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
                ? signature["sha256=".Length..]
                : signature;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(computed),
                Encoding.UTF8.GetBytes(supplied.ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TymeBank webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<TymeBankWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed TymeBank webhook: type={Type} paymentId={PaymentId}",
                webhookEvent.EventType, webhookEvent.Data?.PaymentId);

            var status = webhookEvent.EventType?.ToLowerInvariant() switch
            {
                "payment.completed" or "payment.succeeded" or "payout.completed" => PaymentStatus.Completed,
                "payment.pending" or "payout.pending" => PaymentStatus.Pending,
                "payment.failed" or "payout.failed" => PaymentStatus.Failed,
                "payment.cancelled" or "payment.canceled" => PaymentStatus.Cancelled,
                "payment.refunded" or "refund.completed" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            var reference = webhookEvent.Data?.PaymentId ?? webhookEvent.Data?.PayoutId;
            if (status is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = status.Value,
                EventType = webhookEvent.EventType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse TymeBank webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_cachedToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to TymeBank failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("TymeBank {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && _cachedTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && _cachedTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
                return;

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "oauth2/token") { Content = form };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "HTTP request to TymeBank oauth2/token failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderUnavailableException(ProviderName, $"TymeBank oauth2/token returned {(int)response.StatusCode}: {body}");

            var token = JsonSerializer.Deserialize<TymeBankTokenResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "TymeBank oauth2/token returned no access_token");

            _cachedToken = token.AccessToken;
            _cachedTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 30));
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "settled" => PaymentStatus.Completed,
        "pending" or "processing" or "queued" => PaymentStatus.Pending,
        "failed" or "rejected" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === TymeBank API response shapes (internal) ===

    private sealed class TymeBankTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } = 3600;
    }

    private sealed class TymeBankPaymentResponse
    {
        [JsonPropertyName("payment_id")] public string? PaymentId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }

    private sealed class TymeBankQrResponse
    {
        [JsonPropertyName("qr_id")] public string? QrId { get; set; }
        [JsonPropertyName("qr_string")] public string? QrString { get; set; }
        [JsonPropertyName("qr_image_url")] public string? QrImageUrl { get; set; }
    }

    private sealed class TymeBankRefundResponse
    {
        [JsonPropertyName("refund_id")] public string? RefundId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class TymeBankPayoutResponse
    {
        [JsonPropertyName("payout_id")] public string? PayoutId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class TymeBankWebhookEvent
    {
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public TymeBankWebhookData? Data { get; set; }
    }

    private sealed class TymeBankWebhookData
    {
        [JsonPropertyName("payment_id")] public string? PaymentId { get; set; }
        [JsonPropertyName("payout_id")] public string? PayoutId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
