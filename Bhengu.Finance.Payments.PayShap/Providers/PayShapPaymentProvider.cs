// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Models.Events;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Internals;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;
using Bhengu.Finance.Payments.PayShap.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayShap.Providers;

/// <summary>
/// PayShap adapter that satisfies the generic <see cref="IPaymentGatewayProvider"/> contract so PayShap
/// can appear alongside other gateways in a provider factory.
/// <para>
/// PayShap is a real-time bank-transfer rail rather than a card gateway, so the generic
/// <see cref="PaymentRequest"/> doesn't carry everything PayShap needs (payer/payee account, bank code,
/// proxy alias, transaction reference). The adapter pulls those from <see cref="PaymentRequest.Metadata"/>
/// using the keys documented below.
/// </para>
/// <para>
/// For richer PayShap operations (proxy resolution, EFT, account verification, multi-step settlement),
/// inject <see cref="IPayShapService"/> directly.
/// </para>
///
/// <h3>Required metadata keys for <see cref="ProcessPaymentAsync"/></h3>
/// <list type="bullet">
///   <item><c>payshap.reference</c> — merchant transaction reference (defaults to a generated GUID).</item>
///   <item><c>payshap.payer.account</c> — initiator (payer) account number.</item>
///   <item><c>payshap.payer.bank_code</c> — initiator bank code.</item>
///   <item><c>payshap.payer.name</c> — initiator name shown on the recipient's statement.</item>
///   <item><c>payshap.payee.account</c> — recipient account number.</item>
///   <item><c>payshap.payee.bank_code</c> — recipient bank code.</item>
///   <item><c>payshap.payee.name</c> — recipient name.</item>
///   <item><c>payshap.payee.identifier_type</c> — optional (MSISDN / EMAIL / ID / BUSINESS / ACCOUNT).</item>
///   <item><c>payshap.payee.identifier_value</c> — the proxy alias value if identifier_type is set.</item>
/// </list>
/// <see cref="PaymentRequest.PaymentMethodToken"/> is treated as the payee proxy alias when no explicit
/// <c>payshap.payee.identifier_value</c> is provided.
/// </summary>
public sealed class PayShapPaymentProvider : IPaymentGatewayProvider
{
    private readonly IPayShapService _payShapService;
    private readonly PayShapSettings _settings;
    private readonly ILogger<PayShapPaymentProvider> _logger;

    public string ProviderName => ProviderNames.PayShap;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.SyncSettlement |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.QrCode;

    public PayShapPaymentProvider(
        IPayShapService payShapService,
        IOptions<PayShapSettings> settings,
        ILogger<PayShapPaymentProvider> logger)
    {
        _payShapService = payShapService ?? throw new ArgumentNullException(nameof(payShapService));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PayShapObservability.ObserveChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct));
    }

    private async Task<PaymentResponse> ProcessPaymentCoreAsync(PaymentRequest request, CancellationToken ct)
    {
        var meta = request.Metadata ?? new Dictionary<string, string>();

        var reference = meta.GetValueOrDefault("payshap.reference") ?? Guid.NewGuid().ToString("N");
        var payerAccount = Required(meta, "payshap.payer.account");
        var payerBankCode = Required(meta, "payshap.payer.bank_code");
        var payerName = Required(meta, "payshap.payer.name");
        var payeeAccount = Required(meta, "payshap.payee.account");
        var payeeBankCode = Required(meta, "payshap.payee.bank_code");
        var payeeName = Required(meta, "payshap.payee.name");
        var identifierType = meta.GetValueOrDefault("payshap.payee.identifier_type") ?? "ACCOUNT";
        var identifierValue = meta.GetValueOrDefault("payshap.payee.identifier_value") ?? request.PaymentMethodToken;

        var rtcRequest = new RtcPaymentRequest
        {
            Amount = request.Amount.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
            Currency = request.Currency,
            Reference = reference,
            Description = request.Description,
            Initiator = new RtcInitiator
            {
                AccountNumber = payerAccount,
                BankCode = payerBankCode,
                Name = payerName
            },
            Recipient = new RtcRecipient
            {
                AccountNumber = payeeAccount,
                BankCode = payeeBankCode,
                Name = payeeName,
                IdentifierType = identifierType,
                IdentifierValue = identifierValue
            }
        };

        RtcPaymentResponse rtc;
        try
        {
            rtc = await _payShapService.InitiateRtcPaymentAsync(rtcRequest).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "PayShap RTC call failed", ex);
        }
        catch (Exception ex) when (ex is not BhenguPaymentException)
        {
            throw new BhenguPaymentException(ProviderName,
                "PayShap RTC payment failed", providerErrorMessage: ex.Message, innerException: ex);
        }

        _logger.LogInformation("PayShap RTC initiated: txn={TxnId} status={Status}", rtc.TransactionId, rtc.Status);

        return new PaymentResponse
        {
            GatewayReference = rtc.TransactionId,
            Status = MapStatus(rtc.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = rtc.ConfirmationMessage
        };
    }

    /// <summary>
    /// PayShap has no refund concept — reversing a payment is a new transfer in the opposite direction.
    /// Consumers needing reversal must build a new <see cref="PaymentRequest"/> with swapped payer / payee
    /// metadata and call <see cref="ProcessPaymentAsync"/> again.
    /// </summary>
    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default) =>
        PayShapObservability.ObserveRefundAsync<RefundResponse>(request?.GatewayReference ?? "n/a", () =>
            throw new BhenguPaymentException(
                ProviderName,
                "PayShap has no refund API. To reverse a transfer, initiate a new RTC payment with payer and payee swapped."));

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_settings.SignatureKey))
        {
            _logger.LogWarning("PayShap SignatureKey not configured — signature verification cannot succeed.");
            PayShapObservability.RecordWebhookVerification(false);
            return false;
        }

        bool verified;
        try
        {
            var expected = PayShapSignatureHelper.GenerateSignature(payload, _settings.SignatureKey);
            verified = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(signature.ToLowerInvariant()),
                System.Text.Encoding.UTF8.GetBytes(expected));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PayShap webhook signature verification raised");
            verified = false;
        }
        PayShapObservability.RecordWebhookVerification(verified);
        return verified;
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return PayShapObservability.ObserveWebhookAsync(() => ParseWebhookCoreAsync(payload, ct));
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload, CancellationToken ct)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<InboundPaymentEvent>(payload);
            if (evt is null) return Task.FromResult<WebhookEvent?>(null);

            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.Data.TransactionId,
                Status = MapStatus(evt.Data.Status),
                EventType = evt.EventType,
                RawPayload = new Dictionary<string, string>
                {
                    ["event_id"] = evt.EventId,
                    ["transaction_id"] = evt.Data.TransactionId,
                    ["status"] = evt.Data.Status,
                    ["reference"] = evt.Data.Reference,
                    ["amount"] = evt.Data.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["currency"] = evt.Data.Currency,
                    ["sender_account"] = evt.Data.SenderAccount,
                    ["receiver_account"] = evt.Data.ReceiverAccount
                }
            });
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse PayShap webhook payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static string Required(IReadOnlyDictionary<string, string> meta, string key)
    {
        if (!meta.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new PaymentDeclinedException("payshap", "missing_metadata",
                $"PayShap requires metadata key '{key}' on PaymentRequest.");
        return value;
    }

    private static PaymentStatus MapStatus(string raw) => raw.ToUpperInvariant() switch
    {
        "COMPLETED" or "SUCCESS" or "SETTLED" => PaymentStatus.Completed,
        "PENDING" or "PROCESSING" or "ACCEPTED" => PaymentStatus.Pending,
        "FAILED" or "REJECTED" or "ERROR" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" or "EXPIRED" => PaymentStatus.Cancelled,
        "REVERSED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };
}
