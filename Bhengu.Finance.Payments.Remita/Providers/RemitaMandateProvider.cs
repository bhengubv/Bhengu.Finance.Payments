// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Remita.Configuration;
using Bhengu.Finance.Payments.Remita.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Remita.Providers;

/// <summary>
/// Remita implementation of <see cref="IMandateProvider"/> backed by the Remita Standing
/// Instructions (SI) mandate API — Nigeria's bank-account-based recurring-debit primitive.
/// </summary>
/// <remarks>
/// <para>Remita SI mandates support FIXED and VARIABLE schedules. The Bhengu adapter creates
/// VARIABLE-amount mandates capped at <see cref="MandateRequest.AmountLimit"/>; callers debit
/// individual amounts via <see cref="ChargeMandateAsync"/>.</para>
/// <para>Authorisation runs out-of-band: the payer's bank confirms the mandate after the merchant
/// initiates it. Until then the mandate is <see cref="MandateStatus.Pending"/>; status updates
/// arrive via webhook (<c>MandateActivatedEvent</c> / <c>MandateCancelledEvent</c>).</para>
/// </remarks>
public sealed class RemitaMandateProvider : BhenguProviderBase, IMandateProvider
{
    private const string MandateSetupPath = "remita/exapp/api/v1/send/api/echannelsvc/echannel/mandate/setup";
    private const string MandateStatusPath = "remita/exapp/api/v1/send/api/echannelsvc/echannel/mandate/status";
    private const string MandateCancelPath = "remita/exapp/api/v1/send/api/echannelsvc/echannel/mandate/cancel";
    private const string MandateDebitPath = "remita/exapp/api/v1/send/api/echannelsvc/echannel/mandate/debit";

    private readonly RemitaHttpClient _http;
    private readonly RemitaOptions _options;
    private readonly RemitaIdempotencyCache _idempotency;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Remita;

    /// <summary>Construct a mandate provider. Designed to be registered via DI.</summary>
    public RemitaMandateProvider(
        HttpClient httpClient,
        IOptions<RemitaOptions> options,
        ILogger<RemitaMandateProvider> logger,
        RemitaIdempotencyCache idempotency)
        : base(logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ServiceTypeId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.ServiceTypeId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(RemitaOptions.ApiKey)} is required");

        _http = new RemitaHttpClient(httpClient, _options, Logger);
    }

    /// <inheritdoc />
    public Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, "mandate_create",
            () => CreateMandateCoreAsync(request, ct), ct);
    }

    private Task<Mandate> CreateMandateCoreAsync(MandateRequest request, CancellationToken ct)
        => RunOperationAsync("create_mandate", async () =>
        {
            // BankAccountToken format: "<bankCode>:<accountNumber>" (matches payout convention).
            var colon = request.BankAccountToken.IndexOf(':');
            if (colon <= 0)
                throw new BhenguPaymentException(ProviderName,
                    "Remita CreateMandateAsync.BankAccountToken must be 'bankCode:accountNumber'.",
                    "invalid_bank_account");

            var payerBankCode = request.BankAccountToken[..colon];
            var payerAccount = request.BankAccountToken[(colon + 1)..];
            var requestRef = $"man-{Guid.NewGuid():N}";
            var startDate = (request.StartAt ?? DateTime.UtcNow).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var endDate = (request.EndAt ?? DateTime.UtcNow.AddYears(5)).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
            var maxAmount = request.AmountLimit.ToString("F2", CultureInfo.InvariantCulture);

            var hash = RemitaHttpClient.Sha512Hex(
                _options.MerchantId + _options.ServiceTypeId + requestRef + maxAmount + _options.ApiKey);

            var body = new
            {
                serviceTypeId = _options.ServiceTypeId,
                merchantId = _options.MerchantId,
                requestId = requestRef,
                payerName = request.Description,
                payerEmail = request.CustomerId,
                payerPhone = (string?)null,
                payerAccount,
                payerBankCode,
                amount = maxAmount,
                maxAmount,
                startDate,
                endDate,
                frequency = "VARIABLE",
                hash
            };

            var json = await _http.SendAsync(HttpMethod.Post, MandateSetupPath, body, "CreateMandate", hash, ct)
                .ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<RemitaMandateResponse>(json, RemitaHttpClient.Json)
                ?? throw new BhenguPaymentException(ProviderName, "Remita mandate-setup returned empty body", "empty_response");

            return new Mandate
            {
                Reference = resp.MandateId ?? resp.RequestId ?? requestRef,
                CustomerId = request.CustomerId,
                Status = MapMandateStatus(resp.Status, resp.StatusCode),
                AmountLimit = request.AmountLimit,
                Currency = request.Currency,
                AuthorisedAt = null,
                AuthorisationUrl = resp.AuthorisationUrl
            };
        }, ct);

    /// <inheritdoc />
    public Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        return RunOperationAsync<Mandate?>("get_mandate", async () =>
        {
            try
            {
                var hash = RemitaHttpClient.Sha512Hex(_options.MerchantId + mandateReference + _options.ApiKey);
                var body = new
                {
                    merchantId = _options.MerchantId,
                    mandateId = mandateReference,
                    hash
                };
                var json = await _http.SendAsync(HttpMethod.Post, MandateStatusPath, body, "GetMandate", hash, ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<RemitaMandateResponse>(json, RemitaHttpClient.Json);
                if (resp is null || string.IsNullOrEmpty(resp.MandateId)) return null;

                return new Mandate
                {
                    Reference = resp.MandateId,
                    CustomerId = resp.PayerEmail ?? string.Empty,
                    Status = MapMandateStatus(resp.Status, resp.StatusCode),
                    AmountLimit = decimal.TryParse(resp.MaxAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var ma) ? ma : 0m,
                    Currency = _options.Currency,
                    AuthorisedAt = resp.AuthorisedAt,
                    AuthorisationUrl = resp.AuthorisationUrl
                };
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return null;
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        return RunOperationAsync("cancel_mandate", async () =>
        {
            try
            {
                var hash = RemitaHttpClient.Sha512Hex(_options.MerchantId + mandateReference + _options.ApiKey);
                var body = new
                {
                    merchantId = _options.MerchantId,
                    mandateId = mandateReference,
                    hash
                };
                var json = await _http.SendAsync(HttpMethod.Post, MandateCancelPath, body, "CancelMandate", hash, ct).ConfigureAwait(false);
                var resp = JsonSerializer.Deserialize<RemitaMandateResponse>(json, RemitaHttpClient.Json);

                return new Mandate
                {
                    Reference = mandateReference,
                    CustomerId = resp?.PayerEmail ?? string.Empty,
                    Status = MandateStatus.Cancelled,
                    AmountLimit = decimal.TryParse(resp?.MaxAmount, NumberStyles.Any, CultureInfo.InvariantCulture, out var ma) ? ma : 0m,
                    Currency = _options.Currency,
                    CancelledAt = DateTime.UtcNow
                };
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Already cancelled — return idempotent success.
                return new Mandate
                {
                    Reference = mandateReference,
                    CustomerId = string.Empty,
                    Status = MandateStatus.Cancelled,
                    AmountLimit = 0m,
                    Currency = _options.Currency,
                    CancelledAt = DateTime.UtcNow
                };
            }
        }, ct);
    }

    /// <inheritdoc />
    public Task<PaymentResponse> ChargeMandateAsync(MandateChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, "mandate_charge",
            () => ChargeMandateCoreAsync(request, ct), ct);
    }

    private Task<PaymentResponse> ChargeMandateCoreAsync(MandateChargeRequest request, CancellationToken ct)
        => RunChargeAsync(request.Currency, async () =>
        {
            var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
            var requestRef = request.IdempotencyKey ?? $"mandate-debit-{Guid.NewGuid():N}";
            var hash = RemitaHttpClient.Sha512Hex(
                _options.MerchantId + request.MandateReference + amount + _options.ApiKey);

            var body = new
            {
                merchantId = _options.MerchantId,
                mandateId = request.MandateReference,
                amount,
                requestId = requestRef,
                narration = request.Description,
                hash
            };

            var json = await _http.SendAsync(HttpMethod.Post, MandateDebitPath, body, "ChargeMandate", hash, ct).ConfigureAwait(false);
            var resp = JsonSerializer.Deserialize<RemitaMandateDebitResponse>(json, RemitaHttpClient.Json)
                ?? throw new BhenguPaymentException(ProviderName, "Remita mandate-debit returned empty body", "empty_response");

            var status = MapDebitStatus(resp.Status, resp.StatusCode);

            return new PaymentResponse
            {
                GatewayReference = resp.TransactionRef ?? requestRef,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = resp.Status
            };
        }, ct);

    private static MandateStatus MapMandateStatus(string? status, string? code)
    {
        var s = status?.ToLowerInvariant();
        return s switch
        {
            "active" or "activated" or "success" or "successful" or "00" => MandateStatus.Active,
            "pending" or "awaiting" or "initiated" or "025" => MandateStatus.Pending,
            "cancelled" or "canceled" or "terminated" => MandateStatus.Cancelled,
            "expired" => MandateStatus.Expired,
            "rejected" or "declined" or "020" => MandateStatus.Rejected,
            "paused" or "suspended" => MandateStatus.Paused,
            _ => code switch
            {
                "00" or "01" => MandateStatus.Active,
                "020" => MandateStatus.Rejected,
                _ => MandateStatus.Pending
            }
        };
    }

    private static PaymentStatus MapDebitStatus(string? status, string? code)
    {
        var s = status?.ToLowerInvariant();
        return s switch
        {
            "success" or "successful" or "completed" or "00" => PaymentStatus.Completed,
            "pending" or "025" => PaymentStatus.Pending,
            "failed" or "declined" or "020" => PaymentStatus.Failed,
            "cancelled" or "canceled" => PaymentStatus.Cancelled,
            _ => code switch
            {
                "00" or "01" => PaymentStatus.Completed,
                "020" => PaymentStatus.Failed,
                _ => PaymentStatus.Pending
            }
        };
    }

    // === Remita API shapes (internal) ===

    private sealed class RemitaMandateResponse
    {
        [JsonPropertyName("mandateId")] public string? MandateId { get; set; }
        [JsonPropertyName("requestId")] public string? RequestId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
        [JsonPropertyName("authorisationUrl")] public string? AuthorisationUrl { get; set; }
        [JsonPropertyName("authorizationUrl")] public string? AuthorizationUrlAlt { set => AuthorisationUrl = value; }
        [JsonPropertyName("authorisedAt")] public DateTime? AuthorisedAt { get; set; }
        [JsonPropertyName("payerEmail")] public string? PayerEmail { get; set; }
        [JsonPropertyName("maxAmount")] public string? MaxAmount { get; set; }
    }

    private sealed class RemitaMandateDebitResponse
    {
        [JsonPropertyName("transactionRef")] public string? TransactionRef { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("statuscode")] public string? StatusCode { get; set; }
    }
}
