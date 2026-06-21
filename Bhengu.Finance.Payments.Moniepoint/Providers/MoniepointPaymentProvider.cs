// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Bhengu.Finance.Payments.Moniepoint.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Moniepoint.Providers;

/// <summary>
/// Moniepoint payment provider, integrating Moniepoint's developer API — <b>Monnify</b>
/// (<c>api.monnify.com</c>). Hosted checkout (init-transaction → checkout URL), refunds
/// (initiate-refund), single-transfer payouts (disbursements/single), and webhook handling
/// (<c>monnify-signature</c>, HMAC-SHA512). DocsOnly — built from Monnify's published API, not
/// live-verified against a merchant account.
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Built from Monnify's published API; never verified against a live merchant account.")]
public sealed class MoniepointPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly MoniepointHttpClient _http;
    private readonly MoniepointOptions _options;
    private readonly MoniepointIdempotencyCache? _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Moniepoint;

    /// <inheritdoc />
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.Cards |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.RedirectFlow |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public MoniepointPaymentProvider(
        HttpClient httpClient,
        IOptions<MoniepointOptions> options,
        ILogger<MoniepointPaymentProvider> logger,
        MoniepointIdempotencyCache? idempotency = null)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MoniepointOptions.ApiKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MoniepointOptions.SecretKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ContractCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MoniepointOptions.ContractCode)} is required");

        _http = new MoniepointHttpClient(httpClient, _options, Logger);
    }

    /// <inheritdoc />
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "charge",
                () => RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct), ct);
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var paymentReference = request.Metadata?.GetValueOrDefault("reference") ?? request.IdempotencyKey ?? $"mpt-{Guid.NewGuid():N}";

        var requestBody = new
        {
            amount = request.Amount,
            customerName = request.Metadata?.GetValueOrDefault("name") ?? "Bhengu Customer",
            customerEmail = request.Metadata?.GetValueOrDefault("email") ?? "noreply@bhengu.example",
            paymentReference,
            paymentDescription = request.Description,
            currencyCode = request.Currency.ToUpperInvariant(),
            contractCode = _options.ContractCode,
            redirectUrl = _options.RedirectUrl
        };

        var body = await _http.SendAsync(HttpMethod.Post, "api/v1/merchant/transactions/init-transaction",
            requestBody, "InitTransaction", ct).ConfigureAwait(false);
        var env = JsonSerializer.Deserialize<MoniepointHttpClient.MonnifyEnvelope<InitBody>>(body, MoniepointHttpClient.Json);
        EnsureSuccessful(env, "init-transaction");

        var data = env!.ResponseBody!;
        Logger.LogInformation("Monnify init-transaction: txnRef={TxnRef} paymentRef={PayRef}", data.TransactionReference, paymentReference);

        return new PaymentResponse
        {
            GatewayReference = data.TransactionReference ?? paymentReference,
            Status = PaymentStatus.Pending,   // payer completes on the checkout page
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            RedirectUrl = data.CheckoutUrl,
            Message = env.ResponseMessage
        };
    }

    /// <inheritdoc />
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "refund",
                () => RunRefundAsync(request.GatewayReference, () => ProcessRefundCoreAsync(request, ct), ct), ct);
    }

    private async Task<RefundResponse> ProcessRefundCoreAsync(RefundRequest request, CancellationToken ct)
    {
        var refundReference = request.IdempotencyKey ?? $"mpt-rf-{Guid.NewGuid():N}";
        var requestBody = new
        {
            transactionReference = request.GatewayReference,
            refundReference,
            refundAmount = request.Amount,
            refundReason = request.Reason,
            customerNote = request.Reason
        };

        var body = await _http.SendAsync(HttpMethod.Post, "api/v1/refunds/initiate-refund",
            requestBody, "InitiateRefund", ct).ConfigureAwait(false);
        var env = JsonSerializer.Deserialize<MoniepointHttpClient.MonnifyEnvelope<RefundBody>>(body, MoniepointHttpClient.Json);
        EnsureSuccessful(env, "initiate-refund");

        var data = env!.ResponseBody!;
        Logger.LogInformation("Monnify refund: refundRef={RefundRef} txn={Txn}", data.RefundReference, request.GatewayReference);

        return new RefundResponse
        {
            GatewayReference = data.RefundReference ?? refundReference,
            Amount = request.Amount,
            Status = MapStatus(data.RefundStatus),
            ProcessedAt = DateTime.UtcNow,
            Message = env.ResponseMessage
        };
    }

    /// <inheritdoc />
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency is null
            ? RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct)
            : _idempotency.GetOrAddAsync(request.IdempotencyKey, "payout",
                () => RunPayoutAsync(request.Currency, () => ProcessPayoutCoreAsync(request, ct), ct), ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        // DestinationToken format: "<bankCode>:<accountNumber>".
        var destinationBank = string.Empty;
        var destinationAccount = request.DestinationToken;
        var sep = request.DestinationToken.IndexOf(':');
        if (sep > 0)
        {
            destinationBank = request.DestinationToken[..sep];
            destinationAccount = request.DestinationToken[(sep + 1)..];
        }

        var reference = request.IdempotencyKey ?? $"mpt-tfr-{Guid.NewGuid():N}";
        var requestBody = new
        {
            amount = request.Amount,
            reference,
            narration = request.Description,
            destinationBankCode = destinationBank,
            destinationAccountNumber = destinationAccount,
            currency = request.Currency.ToUpperInvariant(),
            sourceAccountNumber = _options.WalletAccountNumber
        };

        var body = await _http.SendAsync(HttpMethod.Post, "api/v2/disbursements/single",
            requestBody, "SingleDisbursement", ct).ConfigureAwait(false);
        var env = JsonSerializer.Deserialize<MoniepointHttpClient.MonnifyEnvelope<DisbursementBody>>(body, MoniepointHttpClient.Json);
        EnsureSuccessful(env, "disbursements/single");

        var data = env!.ResponseBody!;
        Logger.LogInformation("Monnify disbursement: ref={Reference} status={Status}", data.Reference, data.Status);

        return new PayoutResponse
        {
            GatewayReference = data.Reference ?? reference,
            Status = MapStatus(data.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            var secret = string.IsNullOrWhiteSpace(_options.WebhookSecret) ? _options.SecretKey : _options.WebhookSecret;
            if (string.IsNullOrWhiteSpace(secret))
            {
                Logger.LogWarning("Monnify webhook secret not configured — signature verification cannot succeed.");
                return false;
            }

            // Monnify signs the raw body with HMAC-SHA512 keyed by the secret key, hex-encoded, in monnify-signature.
            using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
            var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
            var a = Encoding.UTF8.GetBytes(computed);
            var b = Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant());
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        });
    }

    /// <inheritdoc />
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        return RunOperationAsync<WebhookEvent?>("parse_webhook", () =>
        {
            try
            {
                var evt = JsonSerializer.Deserialize<MoniepointWebhookEvent>(payload, MoniepointHttpClient.Json);
                if (evt is null) return Task.FromResult<WebhookEvent?>(null);
                return Task.FromResult(MapWebhookEvent(evt));
            }
            catch (JsonException ex)
            {
                Logger.LogError(ex, "Failed to parse Monnify webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    private static WebhookEvent? MapWebhookEvent(MoniepointWebhookEvent evt)
    {
        var data = evt.EventData;
        var reference = data?.TransactionReference ?? data?.Reference;
        if (string.IsNullOrEmpty(reference)) return null;

        var amount = data!.AmountPaid ?? data.Amount ?? 0m;
        var currency = data.CurrencyCode ?? "NGN";
        var type = evt.EventType?.ToUpperInvariant() ?? string.Empty;

        switch (type)
        {
            case "SUCCESSFUL_TRANSACTION":
                return new ChargeSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.ChargeSucceeded,
                    Amount = amount,
                    Currency = currency,
                    CustomerId = data.Customer?.Email,
                    PaymentMethodToken = data.PaymentMethod
                };

            case "FAILED_TRANSACTION":
                return new ChargeFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.ChargeFailed,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data.PaymentStatus,
                    FailureMessage = data.PaymentStatus
                };

            case "SUCCESSFUL_REFUND":
                return new RefundSucceededEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Refunded,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.RefundSucceeded,
                    RefundReference = data.RefundReference ?? reference,
                    Amount = amount,
                    Currency = currency,
                    IsPartial = false
                };

            case "SUCCESSFUL_DISBURSEMENT":
                return new PayoutCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.PayoutCompleted,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    DestinationToken = data.DestinationAccountNumber
                };

            case "FAILED_DISBURSEMENT":
            case "REVERSED_DISBURSEMENT":
                return new PayoutFailedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Failed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.PayoutFailed,
                    PayoutReference = reference,
                    Amount = amount,
                    Currency = currency,
                    FailureCode = data.Status,
                    FailureMessage = data.Status
                };

            case "SETTLEMENT":
                return new SettlementCompletedEvent
                {
                    GatewayReference = reference,
                    Status = PaymentStatus.Completed,
                    EventType = evt.EventType,
                    Category = WebhookEventCategory.SettlementCompleted,
                    SettlementReference = reference,
                    NetAmount = data.SettlementAmount ?? amount,
                    Currency = currency
                };

            default:
                return null;
        }
    }

    private static void EnsureSuccessful<T>(MoniepointHttpClient.MonnifyEnvelope<T>? env, string operation)
    {
        if (env?.RequestSuccessful != true || env.ResponseBody is null)
            throw new PaymentDeclinedException(ProviderNames.Moniepoint, env?.ResponseCode ?? "unsuccessful",
                $"Monnify {operation} was not successful: {env?.ResponseMessage}");
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "PAID" or "OVERPAID" or "SUCCESS" or "SUCCESSFUL" or "COMPLETED" => PaymentStatus.Completed,
        "PENDING" or "PARTIALLY_PAID" or "IN_PROGRESS" or "AWAITING_PAYMENT" or "PROCESSING" => PaymentStatus.Pending,
        "FAILED" or "EXPIRED" or "DECLINED" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" => PaymentStatus.Cancelled,
        "REVERSED" or "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Monnify response / webhook shapes (internal) ===

    private sealed class InitBody
    {
        public string? TransactionReference { get; set; }
        public string? PaymentReference { get; set; }
        public string? CheckoutUrl { get; set; }
    }

    private sealed class RefundBody
    {
        public string? RefundReference { get; set; }
        public string? TransactionReference { get; set; }
        public decimal RefundAmount { get; set; }
        public string? RefundStatus { get; set; }
    }

    private sealed class DisbursementBody
    {
        public string? Reference { get; set; }
        public string? Status { get; set; }
        public decimal Amount { get; set; }
    }

    private sealed class MoniepointWebhookEvent
    {
        [JsonPropertyName("eventType")] public string? EventType { get; set; }
        [JsonPropertyName("eventData")] public MoniepointWebhookData? EventData { get; set; }
    }

    private sealed class MoniepointWebhookData
    {
        public string? TransactionReference { get; set; }
        public string? PaymentReference { get; set; }
        public string? Reference { get; set; }
        public decimal? AmountPaid { get; set; }
        public decimal? Amount { get; set; }
        public decimal? SettlementAmount { get; set; }
        public string? CurrencyCode { get; set; }
        public string? PaymentStatus { get; set; }
        public string? Status { get; set; }
        public string? PaymentMethod { get; set; }
        public string? RefundReference { get; set; }
        public string? DestinationAccountNumber { get; set; }
        public MoniepointWebhookCustomer? Customer { get; set; }
    }

    private sealed class MoniepointWebhookCustomer
    {
        public string? Email { get; set; }
        public string? Name { get; set; }
    }
}
