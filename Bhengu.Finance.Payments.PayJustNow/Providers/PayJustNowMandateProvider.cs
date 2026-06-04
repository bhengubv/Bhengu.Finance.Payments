// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Bhengu.Finance.Payments.PayJustNow.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mandate = Bhengu.Finance.Payments.Core.Models.Mandate.Mandate;

namespace Bhengu.Finance.Payments.PayJustNow.Providers;

/// <summary>
/// PayJustNow implementation of <see cref="IMandateProvider"/>. Every BNPL agreement has an
/// underlying debit-order authorisation against the payer's bank account or card-on-file; this
/// provider exposes the agreement lifecycle and per-instalment pull-debit endpoint as a
/// normalised mandate.
/// </summary>
public sealed class PayJustNowMandateProvider : IMandateProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayJustNowOptions _options;
    private readonly ILogger<PayJustNowMandateProvider> _logger;
    private readonly PayJustNowIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.PayJustNow;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PayJustNowMandateProvider(
        HttpClient httpClient,
        IOptions<PayJustNowOptions> options,
        ILogger<PayJustNowMandateProvider> logger,
        PayJustNowIdempotencyCache idempotency)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayJustNowOptions.ApiKey)} is required");

        PayJustNowHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => CreateMandateCoreAsync(request, ct), ct);
    }

    private async Task<Mandate> CreateMandateCoreAsync(MandateRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "mandate.create");
        var body = new
        {
            shopper_reference = request.CustomerId,
            bank_account_token = request.BankAccountToken,
            amount_limit = (long)(request.AmountLimit * 100m),
            currency = request.Currency.ToUpperInvariant(),
            description = request.Description,
            start_at = request.StartAt?.ToString("o", CultureInfo.InvariantCulture),
            end_at = request.EndAt?.ToString("o", CultureInfo.InvariantCulture)
        };

        var responseBody = await PayJustNowHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, "agreements", body, "CreateMandate", ct, request.IdempotencyKey).ConfigureAwait(false);
        var mandate = JsonSerializer.Deserialize<PjnAgreement>(responseBody, PayJustNowHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "PayJustNow agreement create returned no payload", "no_mandate_data");

        return MapMandate(mandate, request);
    }

    /// <inheritdoc/>
    public async Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        try
        {
            var responseBody = await PayJustNowHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Get, $"agreements/{Uri.EscapeDataString(mandateReference)}", null, "GetMandate", ct).ConfigureAwait(false);
            var mandate = JsonSerializer.Deserialize<PjnAgreement>(responseBody, PayJustNowHttpClient.Json);
            return mandate is null ? null : MapMandate(mandate, null);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "mandate.cancel");
        try
        {
            var responseBody = await PayJustNowHttpClient.SendAsync(
                _httpClient, _logger, HttpMethod.Post, $"agreements/{Uri.EscapeDataString(mandateReference)}/cancel",
                new { }, "CancelMandate", ct).ConfigureAwait(false);
            var mandate = JsonSerializer.Deserialize<PjnAgreement>(responseBody, PayJustNowHttpClient.Json);
            if (mandate is not null) return MapMandate(mandate, null);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true)
        {
            // idempotent cancel — already cancelled = success.
        }

        return new Mandate
        {
            Reference = mandateReference,
            CustomerId = string.Empty,
            Status = MandateStatus.Cancelled,
            AmountLimit = 0m,
            Currency = "ZAR",
            CancelledAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ChargeMandateAsync(MandateChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ChargeMandateCoreAsync(request, ct), ct);
    }

    private async Task<PaymentResponse> ChargeMandateCoreAsync(MandateChargeRequest request, CancellationToken ct)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "mandate.charge");
        var body = new
        {
            agreement_id = request.MandateReference,
            amount = (long)(request.Amount * 100m),
            currency = request.Currency.ToUpperInvariant(),
            description = request.Description
        };

        var responseBody = await PayJustNowHttpClient.SendAsync(
            _httpClient, _logger, HttpMethod.Post, $"agreements/{Uri.EscapeDataString(request.MandateReference)}/charges",
            body, "ChargeMandate", ct, request.IdempotencyKey).ConfigureAwait(false);
        var charge = JsonSerializer.Deserialize<PjnAgreementCharge>(responseBody, PayJustNowHttpClient.Json)
            ?? throw new BhenguPaymentException(ProviderName, "PayJustNow agreement charge returned no payload", "no_charge_data");

        var status = MapStatus(charge.Status);
        if (status == PaymentStatus.Failed)
            throw new PaymentDeclinedException(ProviderName, charge.Status, charge.Message ?? "Mandate debit declined.");

        return new PaymentResponse
        {
            GatewayReference = charge.Id ?? request.MandateReference,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = charge.Status
        };
    }

    private static Mandate MapMandate(PjnAgreement m, MandateRequest? fallback) => new()
    {
        Reference = m.Id ?? string.Empty,
        CustomerId = m.ShopperReference ?? fallback?.CustomerId ?? string.Empty,
        Status = MapMandateStatus(m.Status),
        AmountLimit = m.AmountLimit.HasValue ? m.AmountLimit.Value / 100m : fallback?.AmountLimit ?? 0m,
        Currency = m.Currency ?? fallback?.Currency ?? "ZAR",
        AuthorisedAt = m.AuthorisedAt,
        CancelledAt = m.CancelledAt,
        AuthorisationUrl = m.AuthorisationUrl
    };

    private static MandateStatus MapMandateStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" or "authorised" or "authorized" => MandateStatus.Active,
        "pending" or "awaiting_authorisation" => MandateStatus.Pending,
        "paused" or "suspended" => MandateStatus.Paused,
        "cancelled" or "canceled" => MandateStatus.Cancelled,
        "expired" => MandateStatus.Expired,
        "rejected" or "declined" => MandateStatus.Rejected,
        _ => MandateStatus.Pending
    };

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "succeeded" or "captured" or "approved" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    private sealed class PjnAgreement
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("shopper_reference")] public string? ShopperReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount_limit")] public long? AmountLimit { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("authorised_at")] public DateTime? AuthorisedAt { get; set; }
        [JsonPropertyName("cancelled_at")] public DateTime? CancelledAt { get; set; }
        [JsonPropertyName("authorisation_url")] public string? AuthorisationUrl { get; set; }
    }

    private sealed class PjnAgreementCharge
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
