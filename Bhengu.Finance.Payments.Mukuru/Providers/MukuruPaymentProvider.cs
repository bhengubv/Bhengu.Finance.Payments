// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Mukuru.Providers;

/// <summary>
/// Mukuru B2B remittance provider. South Africa outbound to Zimbabwe, Malawi, Mozambique,
/// Zambia, Ghana, Kenya, Uganda, Nigeria, Tanzania, and Cote d'Ivoire — cash pickup, mobile
/// money, and bank transfer. <see cref="IPayoutProvider.ProcessPayoutAsync"/> wraps
/// Create-Transaction (Mukuru's primary use case);
/// <see cref="IPaymentGatewayProvider.ProcessPaymentAsync"/> wraps Wallet-Topup;
/// <see cref="IPaymentGatewayProvider.ProcessRefundAsync"/> wraps Cancel-Transaction
/// (valid only before the recipient collects). Webhook authenticity uses HMAC-SHA256 in
/// <c>X-Mukuru-Signature</c>.
/// </summary>
public sealed class MukuruPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly MukuruOptions _options;
    private readonly ILogger<MukuruPaymentProvider> _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;

    public string ProviderName => ProviderNames.Mukuru;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.BankTransfer;

    public MukuruPaymentProvider(
        HttpClient httpClient,
        IOptions<MukuruOptions> options,
        ILogger<MukuruPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MukuruOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MukuruOptions.ClientSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-sandbox.mukuru.com"
                : _options.BaseUrl ?? "https://api.mukuru.com";
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
        }
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mukuru is one-direction (SA outbound). ProcessPaymentAsync maps to wallet top-up —
        // i.e. funding the merchant's Mukuru balance from EFT or card so payouts can be drawn.
        var requestBody = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            payment_method = request.Metadata?.GetValueOrDefault("payment_method") ?? "EFT",
            reference = request.PaymentMethodToken
        };

        var body = await SendAsync(HttpMethod.Post, "v1/wallet/topup", requestBody, ct, "ProcessPayment")
            .ConfigureAwait(false);
        var topupResponse = JsonSerializer.Deserialize<MukuruTopupResponse>(body);

        _logger.LogInformation("Mukuru wallet topup: ref={Ref} status={Status}",
            topupResponse?.Reference, topupResponse?.Status);

        return new PaymentResponse
        {
            GatewayReference = topupResponse?.TransactionId ?? request.PaymentMethodToken,
            Status = MapStatus(topupResponse?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = topupResponse?.Message
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Mukuru does not refund collected transactions; cancelling a transaction before
        // recipient collection is the closest analogue. Mapped here as RefundResponse.
        var path = $"v1/transactions/{Uri.EscapeDataString(request.GatewayReference)}/cancel";
        var body = await SendAsync(HttpMethod.Get, path, null, ct, "ProcessRefund").ConfigureAwait(false);
        var cancelResponse = JsonSerializer.Deserialize<MukuruCancelResponse>(body);

        _logger.LogInformation("Mukuru transaction cancellation: txId={TxId} status={Status}",
            request.GatewayReference, cancelResponse?.Status);

        return new RefundResponse
        {
            GatewayReference = cancelResponse?.TransactionId ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(cancelResponse?.Status),
            ProcessedAt = DateTime.UtcNow,
            Message = cancelResponse?.Message
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken format: "<recipientCountry>:<payoutMethod>:<accountOrMsisdn>[:bankCode]"
        // e.g. "ZW:CASH_PICKUP:" or "MW:MOBILE_MONEY:265888123456" or "ZM:BANK:0123456789:ZNB".
        var parts = request.DestinationToken.Split(':');
        if (parts.Length < 3)
            throw new BhenguPaymentException(ProviderName,
                "Mukuru PayoutRequest.DestinationToken must be 'country:payoutMethod:account[:bankCode]'.",
                providerErrorCode: "invalid_destination");

        var recipientCountry = parts[0];
        var payoutMethod = parts[1];
        var account = parts[2];
        var bankCode = parts.Length > 3 ? parts[3] : string.Empty;

        var reference = $"mukuru-{Guid.NewGuid():N}";

        var requestBody = new
        {
            sender = new
            {
                country = _options.SenderCountry,
                first_name = "Bhengu",
                last_name = "Merchant"
            },
            recipient = new
            {
                country = recipientCountry,
                first_name = "Recipient",
                last_name = "Beneficiary",
                msisdn = payoutMethod.Equals("MOBILE_MONEY", StringComparison.OrdinalIgnoreCase) ? account : null,
                payout_method = payoutMethod.ToUpperInvariant(),
                bank_code = bankCode,
                account_number = payoutMethod.Equals("BANK", StringComparison.OrdinalIgnoreCase) ? account : null
            },
            send_amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            send_currency = _options.DefaultCurrency,
            receive_currency = request.Currency.ToUpperInvariant(),
            purpose_of_transfer = request.Description,
            reference
        };

        var body = await SendAsync(HttpMethod.Post, "v1/transactions", requestBody, ct, "ProcessPayout")
            .ConfigureAwait(false);
        var txResponse = JsonSerializer.Deserialize<MukuruTransactionResponse>(body);

        _logger.LogInformation("Mukuru transaction created: txId={TxId} status={Status}",
            txResponse?.TransactionId, txResponse?.Status);

        return new PayoutResponse
        {
            GatewayReference = txResponse?.TransactionId ?? reference,
            Status = MapStatus(txResponse?.Status),
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
            _logger.LogWarning("Mukuru WebhookSecret not configured — signature verification cannot succeed.");
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
            _logger.LogError(ex, "Mukuru webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<MukuruWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Mukuru webhook: type={Type} txId={TxId}",
                webhookEvent.EventType, webhookEvent.Data?.TransactionId);

            var status = webhookEvent.EventType?.ToLowerInvariant() switch
            {
                "transaction.completed" or "transaction.paid" or "transaction.collected" => PaymentStatus.Completed,
                "transaction.pending" or "transaction.created" => PaymentStatus.Pending,
                "transaction.failed" or "transaction.rejected" => PaymentStatus.Failed,
                "transaction.cancelled" or "transaction.canceled" => PaymentStatus.Cancelled,
                "transaction.refunded" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            var reference = webhookEvent.Data?.TransactionId;
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
            _logger.LogError(ex, "Failed to parse Mukuru webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (!string.IsNullOrEmpty(_cachedToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Mukuru failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Mukuru {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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

            var tokenBody = new
            {
                grant_type = "client_credentials",
                client_id = _options.ClientId,
                client_secret = _options.ClientSecret,
                scope = "transactions wallet"
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "auth/token")
            {
                Content = new StringContent(JsonSerializer.Serialize(tokenBody), Encoding.UTF8, "application/json")
            };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "HTTP request to Mukuru auth/token failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderUnavailableException(ProviderName, $"Mukuru auth/token returned {(int)response.StatusCode}: {body}");

            var token = JsonSerializer.Deserialize<MukuruTokenResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "Mukuru auth/token returned no access_token");

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
        "success" or "successful" or "completed" or "paid" or "collected" => PaymentStatus.Completed,
        "pending" or "processing" or "queued" or "created" => PaymentStatus.Pending,
        "failed" or "rejected" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Mukuru API response shapes (internal) ===

    private sealed class MukuruTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } = 3600;
    }

    private sealed class MukuruTransactionResponse
    {
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }

    private sealed class MukuruTopupResponse
    {
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class MukuruCancelResponse
    {
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    private sealed class MukuruWebhookEvent
    {
        [JsonPropertyName("event_type")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public MukuruWebhookData? Data { get; set; }
    }

    private sealed class MukuruWebhookData
    {
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
