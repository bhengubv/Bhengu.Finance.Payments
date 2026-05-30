// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

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
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PagSeguro.Providers;

/// <summary>
/// PagSeguro / PagBank (Brazil) payment gateway provider. Wraps the PagBank v4 REST API
/// for card, PIX, boleto and wallet payments, refunds and bank-transfer payouts.
/// PagBank models payments as <c>orders</c> with one-or-more <c>charges</c>.
/// </summary>
public sealed class PagSeguroPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly PagSeguroOptions _options;
    private readonly ILogger<PagSeguroPaymentProvider> _logger;

    public string ProviderName => ProviderNames.PagSeguro;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer;

    public PagSeguroPaymentProvider(
        HttpClient httpClient,
        IOptions<PagSeguroOptions> options,
        ILogger<PagSeguroPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PagSeguroOptions.ApiToken)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? (_options.SandboxUrl ?? "https://sandbox.api.pagseguro.com")
                : (_options.BaseUrl ?? "https://api.pagseguro.com");
            _httpClient.BaseAddress = new Uri(resolved);
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, string>();
        var paymentType = (metadata.TryGetValue("payment_method_type", out var pmt) ? pmt : "CREDIT_CARD").ToUpperInvariant();
        var amountInCents = (long)(request.Amount * 100);
        var referenceId = metadata.TryGetValue("reference_id", out var rid) ? rid : $"pagseguro-{Guid.NewGuid():N}";
        var currency = (request.Currency ?? _options.Currency).ToUpperInvariant();

        var paymentMethod = new Dictionary<string, object?>
        {
            ["type"] = paymentType,
            ["installments"] = metadata.TryGetValue("installments", out var inst) && int.TryParse(inst, out var i) ? i : 1,
            ["capture"] = true
        };

        if (paymentType == "CREDIT_CARD")
        {
            paymentMethod["card"] = new Dictionary<string, object?>
            {
                ["encrypted"] = request.PaymentMethodToken,
                ["store"] = metadata.TryGetValue("store_card", out var sc) && bool.TryParse(sc, out var b) && b,
                ["holder"] = new { name = metadata.GetValueOrDefault("holder_name") }
            };
        }

        var charges = new[]
        {
            new Dictionary<string, object?>
            {
                ["reference_id"] = referenceId,
                ["description"] = request.Description,
                ["amount"] = new { value = amountInCents, currency },
                ["payment_method"] = paymentMethod
            }
        };

        var requestBody = new Dictionary<string, object?>
        {
            ["reference_id"] = referenceId,
            ["customer"] = new Dictionary<string, object?>
            {
                ["name"] = metadata.GetValueOrDefault("customer_name"),
                ["email"] = metadata.GetValueOrDefault("customer_email"),
                ["tax_id"] = metadata.GetValueOrDefault("customer_tax_id")
            },
            ["items"] = new[]
            {
                new
                {
                    reference_id = referenceId,
                    name = request.Description,
                    quantity = 1,
                    unit_amount = amountInCents
                }
            },
            ["charges"] = charges,
            ["notification_urls"] = _options.NotificationUrl is null ? Array.Empty<string>() : new[] { _options.NotificationUrl }
        };

        var (body, _) = await SendAsync(HttpMethod.Post, "/orders", requestBody, ct, "ProcessPayment").ConfigureAwait(false);
        var orderResponse = JsonSerializer.Deserialize<PagSeguroOrderResponse>(body);

        var firstCharge = orderResponse?.Charges?.FirstOrDefault();
        _logger.LogInformation("PagSeguro order created: {OrderId} chargeStatus={Status}",
            orderResponse?.Id, firstCharge?.Status);

        return new PaymentResponse
        {
            GatewayReference = orderResponse?.Id ?? string.Empty,
            Status = MapStatus(firstCharge?.Status ?? "WAITING"),
            Amount = (firstCharge?.Amount?.Value ?? amountInCents) / 100m,
            Currency = firstCharge?.Amount?.Currency ?? currency,
            ProcessedAt = DateTime.UtcNow,
            Message = firstCharge?.Status
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // PagSeguro: POST /charges/{id}/cancel with the amount to cancel. GatewayReference here is the CHARGE id.
        var amountInCents = (long)(request.Amount * 100);
        var requestBody = new
        {
            amount = new { value = amountInCents, currency = _options.Currency }
        };

        var (body, _) = await SendAsync(
            HttpMethod.Post,
            $"/charges/{request.GatewayReference}/cancel",
            requestBody,
            ct,
            "ProcessRefund").ConfigureAwait(false);

        var refundResponse = JsonSerializer.Deserialize<PagSeguroChargeResponse>(body);

        _logger.LogInformation("PagSeguro charge cancelled: {ChargeId} status={Status}",
            refundResponse?.Id, refundResponse?.Status);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Id ?? string.Empty,
            Amount = (refundResponse?.Amount?.Value ?? amountInCents) / 100m,
            Status = MapStatus(refundResponse?.Status ?? "CANCELED"),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // PagSeguro bank-transfer payout: POST /transfers. DestinationToken is expected to be
        // a serialised "branch|number|check_digit|tax_id|holder|bank_code" account identifier the merchant supplied.
        var parts = request.DestinationToken.Split('|');
        if (parts.Length < 6)
            throw new BhenguPaymentException(
                ProviderName,
                "PagSeguro payout DestinationToken must be 'branch|number|check_digit|holder_tax_id|holder_name|bank_code'",
                providerErrorCode: "invalid_destination_token");

        var amountInCents = (long)(request.Amount * 100);
        var requestBody = new
        {
            reference_id = $"transfer-{Guid.NewGuid():N}",
            account = new
            {
                branch = parts[0],
                number = parts[1],
                check_digit = parts[2],
                holder_tax_id = parts[3],
                holder_name = parts[4],
                bank_code = parts[5]
            },
            amount = new { value = amountInCents, currency = request.Currency }
        };

        var (body, _) = await SendAsync(HttpMethod.Post, "/transfers", requestBody, ct, "ProcessPayout").ConfigureAwait(false);
        var payoutResponse = JsonSerializer.Deserialize<PagSeguroTransferResponse>(body);

        _logger.LogInformation("PagSeguro transfer created: {TransferId} status={Status}",
            payoutResponse?.Id, payoutResponse?.Status);

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.Id ?? string.Empty,
            Status = MapStatus(payoutResponse?.Status ?? "PROCESSING"),
            Amount = (payoutResponse?.Amount?.Value ?? amountInCents) / 100m,
            Currency = payoutResponse?.Amount?.Currency ?? request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("PagSeguro WebhookSecret not configured — signature verification cannot succeed.");
            return false;
        }

        try
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
            var computedHex = Convert.ToHexString(computedHash).ToLowerInvariant();

            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                Encoding.UTF8.GetBytes(computedHex));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PagSeguro webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<PagSeguroWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            // PagBank notifications carry an order id plus the first charge's status.
            // Some events are order-shaped, others are charge-shaped (cancellations come back as a charge object).
            var firstCharge = webhookEvent.Charges?.FirstOrDefault();
            var status = (firstCharge?.Status ?? webhookEvent.Status ?? string.Empty).ToUpperInvariant();
            var reference = webhookEvent.Id ?? firstCharge?.Id;

            _logger.LogInformation("Parsed PagSeguro webhook: ref={Ref} status={Status}", reference, status);

            var mappedStatus = status switch
            {
                "PAID" or "AUTHORIZED" or "CAPTURED" => PaymentStatus.Completed,
                "WAITING" or "IN_ANALYSIS" or "PENDING" => PaymentStatus.Pending,
                "DECLINED" or "FAILED" => PaymentStatus.Failed,
                "CANCELED" or "CANCELLED" or "VOIDED" => PaymentStatus.Cancelled,
                "REFUNDED" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            if (mappedStatus is null || string.IsNullOrEmpty(reference))
                return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = reference,
                Status = mappedStatus.Value,
                EventType = status
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse PagSeguro webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<(string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PagSeguro failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PagSeguro {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return (responseBody, response);
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToUpperInvariant() switch
    {
        "PAID" or "AUTHORIZED" or "CAPTURED" or "COMPLETED" => PaymentStatus.Completed,
        "WAITING" or "IN_ANALYSIS" or "PENDING" or "PROCESSING" => PaymentStatus.Pending,
        "DECLINED" or "FAILED" => PaymentStatus.Failed,
        "CANCELED" or "CANCELLED" or "VOIDED" => PaymentStatus.Cancelled,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === PagSeguro API response shapes (internal) ===

    private sealed class PagSeguroOrderResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("charges")] public PagSeguroChargeResponse[]? Charges { get; set; }
    }

    private sealed class PagSeguroChargeResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public PagSeguroAmount? Amount { get; set; }
    }

    private sealed class PagSeguroAmount
    {
        [JsonPropertyName("value")] public long Value { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class PagSeguroTransferResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public PagSeguroAmount? Amount { get; set; }
    }

    private sealed class PagSeguroWebhookEvent
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("charges")] public PagSeguroChargeResponse[]? Charges { get; set; }
    }
}
