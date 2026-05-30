// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Stitch.Providers;

/// <summary>
/// Stitch (South Africa open-banking) pay-by-bank, InstantEFT, LinkPay, and payout provider.
/// Covers FNB, ABSA, Standard Bank, Nedbank, Capitec, Investec, and Discovery via the Stitch
/// GraphQL API. <see cref="IPaymentGatewayProvider.ProcessPaymentAsync"/> issues
/// <c>clientPaymentInitiationRequestCreate</c>; <see cref="IPayoutProvider.ProcessPayoutAsync"/>
/// issues <c>clientPayoutInitiationRequestCreate</c>. Refunds use the REST endpoint
/// <c>POST /api/v1/payments/{id}/refund</c>. Webhook authenticity uses HMAC-SHA256 in
/// <c>X-Stitch-Signature</c>.
/// </summary>
public sealed class StitchPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly StitchOptions _options;
    private readonly ILogger<StitchPaymentProvider> _logger;
    private readonly Uri _graphqlUri;

    public string ProviderName => ProviderNames.Stitch;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.BankTransfer;

    public StitchPaymentProvider(
        HttpClient httpClient,
        IOptions<StitchOptions> options,
        ILogger<StitchPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey) && string.IsNullOrWhiteSpace(_options.ClientAssertionJwt))
            throw new ProviderConfigurationException(ProviderName,
                $"{nameof(StitchOptions.ApiKey)} or {nameof(StitchOptions.ClientAssertionJwt)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-staging.stitch.money"
                : _options.BaseUrl ?? "https://api.stitch.money";
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
        }

        _graphqlUri = Uri.TryCreate(_options.GraphqlEndpoint, UriKind.Absolute, out var abs)
            ? abs
            : new Uri(_httpClient.BaseAddress, "graphql");
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.BeneficiaryAccountNumber) ||
            string.IsNullOrWhiteSpace(_options.BeneficiaryBankId))
            throw new ProviderConfigurationException(ProviderName,
                "Stitch payments require BeneficiaryAccountNumber and BeneficiaryBankId.");

        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var payerReference = request.Metadata?.GetValueOrDefault("payer_reference") ?? request.PaymentMethodToken;
        var beneficiaryReference = request.Metadata?.GetValueOrDefault("beneficiary_reference") ?? request.PaymentMethodToken;

        var query = """
            mutation ClientPaymentInit(
              $amount: MoneyInput!, $payerReference: String!, $beneficiaryReference: String!,
              $externalReference: String!, $beneficiary: BeneficiaryInput!
            ) {
              clientPaymentInitiationRequestCreate(input: {
                amount: $amount,
                payerReference: $payerReference,
                beneficiaryReference: $beneficiaryReference,
                externalReference: $externalReference,
                beneficiary: $beneficiary
              }) {
                paymentInitiationRequest { id url }
              }
            }
            """;

        var variables = new
        {
            amount = new { quantity = amount, currency = request.Currency.ToUpperInvariant() },
            payerReference,
            beneficiaryReference,
            externalReference = request.PaymentMethodToken,
            beneficiary = new
            {
                bankAccount = new
                {
                    name = _options.BeneficiaryName,
                    bankId = _options.BeneficiaryBankId,
                    accountNumber = _options.BeneficiaryAccountNumber,
                    accountType = "current",
                    beneficiaryType = "private"
                }
            }
        };

        var body = await SendGraphqlAsync(query, variables, ct, "ProcessPayment").ConfigureAwait(false);
        var graphqlResponse = JsonSerializer.Deserialize<StitchGraphqlResponse<StitchPaymentInitData>>(body);

        var pir = graphqlResponse?.Data?.ClientPaymentInitiationRequestCreate?.PaymentInitiationRequest;
        _logger.LogInformation("Stitch payment-init created: id={Id} url={Url}", pir?.Id, pir?.Url);

        return new PaymentResponse
        {
            GatewayReference = pir?.Id ?? request.PaymentMethodToken,
            Status = PaymentStatus.Pending,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = pir?.Url is { Length: > 0 } u ? u : null,
            Message = "Pay-by-bank initiated"
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // DestinationToken format: "<bankId>:<accountNumber>:<beneficiaryName>"
        var parts = request.DestinationToken.Split(':');
        if (parts.Length < 3)
            throw new BhenguPaymentException(ProviderName,
                "Stitch PayoutRequest.DestinationToken must be 'bankId:accountNumber:beneficiaryName'.",
                providerErrorCode: "invalid_destination");

        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);

        var query = """
            mutation ClientPayoutInit(
              $amount: MoneyInput!, $beneficiaryReference: String!, $externalReference: String!,
              $beneficiary: BeneficiaryInput!
            ) {
              clientPayoutInitiationRequestCreate(input: {
                amount: $amount,
                beneficiaryReference: $beneficiaryReference,
                externalReference: $externalReference,
                beneficiary: $beneficiary
              }) {
                payoutInitiationRequest { id status }
              }
            }
            """;

        var externalReference = $"stitch-payout-{Guid.NewGuid():N}";
        var variables = new
        {
            amount = new { quantity = amount, currency = request.Currency.ToUpperInvariant() },
            beneficiaryReference = request.Description,
            externalReference,
            beneficiary = new
            {
                bankAccount = new
                {
                    name = parts[2],
                    bankId = parts[0],
                    accountNumber = parts[1],
                    accountType = "current",
                    beneficiaryType = "private"
                }
            }
        };

        var body = await SendGraphqlAsync(query, variables, ct, "ProcessPayout").ConfigureAwait(false);
        var graphqlResponse = JsonSerializer.Deserialize<StitchGraphqlResponse<StitchPayoutInitData>>(body);

        var pir = graphqlResponse?.Data?.ClientPayoutInitiationRequestCreate?.PayoutInitiationRequest;
        _logger.LogInformation("Stitch payout-init created: id={Id} status={Status}", pir?.Id, pir?.Status);

        return new PayoutResponse
        {
            GatewayReference = pir?.Id ?? externalReference,
            Status = MapStatus(pir?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
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

        var path = $"api/v1/payments/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendRestAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<StitchRefundResponse>(body);

        _logger.LogInformation("Stitch refund: refundId={RefundId} for payment {PaymentId}",
            refundResponse?.Id, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = refundResponse?.Id ?? request.GatewayReference,
            Amount = request.Amount,
            Status = MapStatus(refundResponse?.Status),
            ProcessedAt = DateTime.UtcNow,
            Message = refundResponse?.Status
        };
    }

    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            _logger.LogWarning("Stitch WebhookSecret not configured — signature verification cannot succeed.");
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
            _logger.LogError(ex, "Stitch webhook signature verification raised");
            return false;
        }
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var webhookEvent = JsonSerializer.Deserialize<StitchWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            _logger.LogInformation("Parsed Stitch webhook: type={Type} id={Id}",
                webhookEvent.EventType, webhookEvent.Data?.Id);

            var status = webhookEvent.EventType?.ToLowerInvariant() switch
            {
                "paymentinitiationrequest.completed" or "payment.completed" or "payment.settled"
                    => PaymentStatus.Completed,
                "paymentinitiationrequest.pending" or "payment.pending" => PaymentStatus.Pending,
                "paymentinitiationrequest.failed" or "payment.failed" or "payment.rejected"
                    => PaymentStatus.Failed,
                "paymentinitiationrequest.cancelled" or "paymentinitiationrequest.canceled"
                    => PaymentStatus.Cancelled,
                "payment.refunded" or "refund.completed" => PaymentStatus.Refunded,
                _ => (PaymentStatus?)null
            };

            var reference = webhookEvent.Data?.Id;
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
            _logger.LogError(ex, "Failed to parse Stitch webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private async Task<string> SendGraphqlAsync(string query, object variables, CancellationToken ct, string operation)
    {
        var payload = new { query, variables };
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, _graphqlUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AttachAuth(req);

        return await ExecuteAsync(req, ct, operation).ConfigureAwait(false);
    }

    private async Task<string> SendRestAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        AttachAuth(req);
        return await ExecuteAsync(req, ct, operation).ConfigureAwait(false);
    }

    private void AttachAuth(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(_options.ApiKey))
            req.Headers.TryAddWithoutValidation("X-API-Key", _options.ApiKey);
        if (!string.IsNullOrEmpty(_options.ClientAssertionJwt))
            req.Headers.TryAddWithoutValidation("X-Client-Assertion", _options.ClientAssertionJwt);
        req.Headers.TryAddWithoutValidation("X-Stitch-Client-Id", _options.ClientId);
    }

    private async Task<string> ExecuteAsync(HttpRequestMessage req, CancellationToken ct, string operation)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stitch failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Stitch {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "settled" => PaymentStatus.Completed,
        "pending" or "processing" or "submitted" => PaymentStatus.Pending,
        "failed" or "rejected" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Stitch API response shapes (internal) ===

    private sealed class StitchGraphqlResponse<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class StitchPaymentInitData
    {
        [JsonPropertyName("clientPaymentInitiationRequestCreate")]
        public StitchPaymentInitCreate? ClientPaymentInitiationRequestCreate { get; set; }
    }

    private sealed class StitchPaymentInitCreate
    {
        [JsonPropertyName("paymentInitiationRequest")] public StitchPir? PaymentInitiationRequest { get; set; }
    }

    private sealed class StitchPir
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class StitchPayoutInitData
    {
        [JsonPropertyName("clientPayoutInitiationRequestCreate")]
        public StitchPayoutInitCreate? ClientPayoutInitiationRequestCreate { get; set; }
    }

    private sealed class StitchPayoutInitCreate
    {
        [JsonPropertyName("payoutInitiationRequest")] public StitchPir? PayoutInitiationRequest { get; set; }
    }

    private sealed class StitchRefundResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class StitchWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("data")] public StitchWebhookData? Data { get; set; }
    }

    private sealed class StitchWebhookData
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
