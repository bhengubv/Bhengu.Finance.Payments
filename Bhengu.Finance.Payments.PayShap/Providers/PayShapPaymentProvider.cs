// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Models.Events;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Models.Responses;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;
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
public sealed class PayShapPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly IPayShapService _payShapService;
    private readonly PayShapSettings _settings;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayShap;

    /// <summary>Capabilities advertised by the PayShap payment provider.</summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.SyncSettlement |
        ProviderCapabilities.BankTransfer |
        ProviderCapabilities.QrCode;

    /// <summary>Construct the provider; the <paramref name="payShapService"/> wraps the underlying HTTP client.</summary>
    public PayShapPaymentProvider(
        IPayShapService payShapService,
        IOptions<PayShapSettings> settings,
        ILogger<PayShapPaymentProvider> logger)
        : base(logger)
    {
        _payShapService = payShapService ?? throw new ArgumentNullException(nameof(payShapService));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, () => ProcessPaymentCoreAsync(request, ct), ct);
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
        catch (Exception ex) when (ex is not BhenguPaymentException and not HttpRequestException)
        {
            // HttpRequestException is auto-translated to ProviderUnavailableException by BhenguProviderBase.
            throw new BhenguPaymentException(ProviderName,
                "PayShap RTC payment failed", providerErrorMessage: ex.Message, innerException: ex);
        }

        Logger.LogInformation("PayShap RTC initiated: txn={TxnId} status={Status}", rtc.TransactionId, rtc.Status);

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
        RunRefundAsync<RefundResponse>(request?.GatewayReference ?? "n/a", () =>
            throw new BhenguPaymentException(
                ProviderName,
                "PayShap has no refund API. To reverse a transfer, initiate a new RTC payment with payer and payee swapped."),
            ct);

    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_settings.SignatureKey))
        {
            Logger.LogWarning("PayShap SignatureKey not configured — signature verification cannot succeed.");
            return RunWebhookVerify(() => false);
        }

        return RunWebhookVerify(() => SignatureHelpers.VerifyHmacSha256(payload, signature, _settings.SignatureKey));
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () => ParseWebhookCoreAsync(payload), ct);
    }

    private Task<WebhookEvent?> ParseWebhookCoreAsync(string payload)
    {
        try
        {
            var evt = JsonSerializer.Deserialize<InboundPaymentEvent>(payload);
            if (evt is null) return Task.FromResult<WebhookEvent?>(null);

            var mappedStatus = MapStatus(evt.Data.Status);
            return Task.FromResult<WebhookEvent?>(new WebhookEvent
            {
                GatewayReference = evt.Data.TransactionId,
                Status = mappedStatus,
                EventType = evt.EventType,
                Category = mappedStatus switch
                {
                    PaymentStatus.Completed => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargeSucceeded,
                    PaymentStatus.Failed => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargeFailed,
                    PaymentStatus.Pending => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargePending,
                    PaymentStatus.Refunded => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.RefundSucceeded,
                    _ => Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.Unknown
                },
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
            Logger.LogError(ex, "Failed to parse PayShap webhook payload");
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
