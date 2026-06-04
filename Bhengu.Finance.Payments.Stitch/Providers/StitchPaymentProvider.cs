// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
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
public sealed class StitchPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly StitchOptions _options;
    private readonly Uri _graphqlUri;

    public override string ProviderName => ProviderNames.Stitch;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.Mandates |
        ProviderCapabilities.TypedWebhooks;

    public StitchPaymentProvider(
        HttpClient httpClient,
        IOptions<StitchOptions> options,
        ILogger<StitchPaymentProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

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

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
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
        Logger.LogInformation("Stitch payment-init created: id={Id} url={Url}", pir?.Id, pir?.Url);

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

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
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
        Logger.LogInformation("Stitch payout-init created: id={Id} status={Status}", pir?.Id, pir?.Status);

        return new PayoutResponse
        {
            GatewayReference = pir?.Id ?? externalReference,
            Status = MapStatus(pir?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var requestBody = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            reason = request.Reason
        };

        var path = $"api/v1/payments/{Uri.EscapeDataString(request.GatewayReference)}/refund";
        var body = await SendRestAsync(HttpMethod.Post, path, requestBody, ct, "ProcessRefund").ConfigureAwait(false);
        var refundResponse = JsonSerializer.Deserialize<StitchRefundResponse>(body);

        Logger.LogInformation("Stitch refund: refundId={RefundId} for payment {PaymentId}",
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

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            Logger.LogWarning("Stitch WebhookSecret not configured — signature verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        // Stitch headers may be prefixed "sha256=<hex>"; strip the prefix before hand-off.
        var supplied = signature.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? signature["sha256=".Length..]
            : signature;
        return RunWebhookVerify(() => SignatureHelpers.VerifyHmacSha256(payload, supplied, _options.WebhookSecret));
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload, ct), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var webhookEvent = JsonSerializer.Deserialize<StitchWebhookEvent>(payload);
            if (webhookEvent is null) return Task.FromResult<WebhookEvent?>(null);

            Logger.LogInformation("Parsed Stitch webhook: type={Type} id={Id}",
                webhookEvent.EventType, webhookEvent.Data?.Id);

            var typed = MapTypedEvent(webhookEvent);
            return Task.FromResult(typed);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to parse Stitch webhook event");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    // Map Stitch event name → strongly-typed Bhengu webhook sub-record. Stitch fires events for
    // pay-by-bank payments (paymentInitiationRequest.*), DebiCheck mandate lifecycle
    // (paymentInitiation.*), DebiCheck debits (debit.*), refunds, and payouts.
    // Unrecognised events return null so consumers don't get false signals.
    private static WebhookEvent? MapTypedEvent(StitchWebhookEvent evt)
    {
        var eventName = evt.EventType?.ToLowerInvariant();
        var data = evt.Data;
        if (string.IsNullOrEmpty(eventName) || data?.Id is null or "") return null;

        var reference = data.Id;
        var amount = decimal.TryParse(data.Amount?.Quantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) ? q : 0m;
        var currency = data.Amount?.Currency ?? "ZAR";

        return eventName switch
        {
            // Pay-by-bank lifecycle — settled
            "paymentinitiationrequest.completed" or "payment.completed" or "payment.settled" or "debit.completed"
                => new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = data.Payer?.Reference,
                    PaymentMethodToken = reference
                },

            // Pay-by-bank lifecycle — pending
            "paymentinitiationrequest.pending" or "payment.pending"
                => new ChargePendingEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Pending,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.ChargePending,
                    Amount = amount,
                    Currency = currency
                },

            // Pay-by-bank lifecycle — failure / decline
            "paymentinitiationrequest.failed" or "payment.failed" or "payment.rejected" or "debit.failed"
                => new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data.Status,
                    FailureMessage = data.Status
                },

            // Pay-by-bank lifecycle — cancellation
            "paymentinitiationrequest.cancelled" or "paymentinitiationrequest.canceled"
                => new WebhookEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Cancelled,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.Unknown
                },

            // Refund lifecycle
            "payment.refunded" or "refund.completed"
                => new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                },

            "refund.failed"
                => new RefundFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.RefundFailed,
                    Amount = amount,
                    Currency = currency
                },

            // DebiCheck mandate lifecycle
            "paymentinitiation.completed" or "paymentinitiation.authorized" or "paymentinitiation.authorised"
            or "mandate.activated" or "mandate.authorized"
                => new MandateActivatedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.MandateActivated,
                    MandateReference = reference,
                    AmountLimit = amount == 0m ? null : amount,
                    Currency = currency
                },

            "paymentinitiation.cancelled" or "paymentinitiation.canceled"
            or "mandate.cancelled" or "mandate.canceled"
                => new MandateCancelledEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Cancelled,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.MandateCancelled,
                    MandateReference = reference,
                    CancellationReason = data.Status ?? "cancelled"
                },

            // Payout lifecycle (clientPayoutInitiationRequest.*)
            "payoutinitiationrequest.completed" or "payout.completed" or "payout.settled"
                => new PayoutCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency
                },

            "payoutinitiationrequest.failed" or "payout.failed"
                => new PayoutFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data.Status
                },

            _ => null
        };
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
        // HttpRequestException is auto-translated to ProviderUnavailableException by BhenguProviderBase.
        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Stitch {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
        [JsonPropertyName("amount")] public StitchWebhookMoney? Amount { get; set; }
        [JsonPropertyName("payer")] public StitchWebhookPayer? Payer { get; set; }
    }

    private sealed class StitchWebhookMoney
    {
        [JsonPropertyName("quantity")] public string? Quantity { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class StitchWebhookPayer
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }
}
