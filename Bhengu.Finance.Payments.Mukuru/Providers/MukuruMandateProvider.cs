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
using Bhengu.Finance.Payments.Mukuru.Configuration;
using Bhengu.Finance.Payments.Mukuru.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mandate = Bhengu.Finance.Payments.Core.Models.Mandate.Mandate;

namespace Bhengu.Finance.Payments.Mukuru.Providers;

/// <summary>
/// Mukuru implementation of <see cref="IMandateProvider"/> backed by the Mukuru Send Recurring
/// Transfer endpoint (<c>/v1/send/recurring</c>) — the payer authorises Mukuru to debit a
/// nominated account on a recurring schedule for outbound corridor remittance.
/// </summary>
/// <remarks>
/// Mandate-charge wraps the recurring-trigger endpoint that fires the next instalment of an
/// active recurring transfer. The returned <see cref="PaymentResponse.GatewayReference"/> is the
/// per-instalment transaction id — distinct from the mandate's parent reference.
/// </remarks>
public sealed class MukuruMandateProvider : IMandateProvider
{
    private readonly MukuruPaymentProvider _payment;
    private readonly MukuruOptions _options;
    private readonly ILogger<MukuruMandateProvider> _logger;
    private readonly MukuruIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Mukuru;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public MukuruMandateProvider(
        MukuruPaymentProvider payment,
        IOptions<MukuruOptions> options,
        ILogger<MukuruMandateProvider> logger,
        MukuruIdempotencyCache idempotency)
    {
        _payment = payment ?? throw new ArgumentNullException(nameof(payment));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));
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
            amount_limit = request.AmountLimit.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            description = request.Description,
            start_at = request.StartAt?.ToString("o", CultureInfo.InvariantCulture),
            end_at = request.EndAt?.ToString("o", CultureInfo.InvariantCulture)
        };

        var response = await _payment.SendAsync(HttpMethod.Post, "v1/send/recurring", body, ct, "CreateMandate").ConfigureAwait(false);
        var mandate = JsonSerializer.Deserialize<MukuruRecurring>(response)
            ?? throw new BhenguPaymentException(ProviderName, "Mukuru recurring create returned no payload", "no_mandate_data");
        return Map(mandate, request);
    }

    /// <inheritdoc/>
    public async Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mandateReference);
        try
        {
            var response = await _payment.SendAsync(HttpMethod.Get, $"v1/send/recurring/{Uri.EscapeDataString(mandateReference)}", null, ct, "GetMandate").ConfigureAwait(false);
            var mandate = JsonSerializer.Deserialize<MukuruRecurring>(response);
            return mandate is null ? null : Map(mandate, null);
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
            var response = await _payment.SendAsync(HttpMethod.Post, $"v1/send/recurring/{Uri.EscapeDataString(mandateReference)}/cancel", new { }, ct, "CancelMandate").ConfigureAwait(false);
            var mandate = JsonSerializer.Deserialize<MukuruRecurring>(response);
            if (mandate is not null) return Map(mandate, null);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) == true)
        {
            // already cancelled = success.
        }

        return new Mandate
        {
            Reference = mandateReference,
            CustomerId = string.Empty,
            Status = MandateStatus.Cancelled,
            AmountLimit = 0m,
            Currency = _options.DefaultCurrency,
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
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            description = request.Description,
            reference = request.IdempotencyKey ?? $"mukuru-debit-{Guid.NewGuid():N}"
        };

        var response = await _payment.SendAsync(HttpMethod.Post,
            $"v1/send/recurring/{Uri.EscapeDataString(request.MandateReference)}/charge", body, ct, "ChargeMandate").ConfigureAwait(false);

        var charge = JsonSerializer.Deserialize<MukuruRecurringCharge>(response)
            ?? throw new BhenguPaymentException(ProviderName, "Mukuru recurring charge returned no payload", "no_charge_data");

        var status = MapStatus(charge.Status);
        if (status == PaymentStatus.Failed)
            throw new PaymentDeclinedException(ProviderName, charge.Status, charge.Message ?? "Mukuru mandate debit declined.");

        return new PaymentResponse
        {
            GatewayReference = charge.TransactionId ?? request.MandateReference,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = charge.Status
        };
    }

    private static Mandate Map(MukuruRecurring m, MandateRequest? fallback) => new()
    {
        Reference = m.Id ?? string.Empty,
        CustomerId = m.ShopperReference ?? fallback?.CustomerId ?? string.Empty,
        Status = MapMandateStatus(m.Status),
        AmountLimit = decimal.TryParse(m.AmountLimit, NumberStyles.Number, CultureInfo.InvariantCulture, out var amt) ? amt : fallback?.AmountLimit ?? 0m,
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
        "succeeded" or "captured" or "approved" or "completed" or "paid" => PaymentStatus.Completed,
        "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "declined" => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    private sealed class MukuruRecurring
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("shopper_reference")] public string? ShopperReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount_limit")] public string? AmountLimit { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("authorised_at")] public DateTime? AuthorisedAt { get; set; }
        [JsonPropertyName("cancelled_at")] public DateTime? CancelledAt { get; set; }
        [JsonPropertyName("authorisation_url")] public string? AuthorisationUrl { get; set; }
    }

    private sealed class MukuruRecurringCharge
    {
        [JsonPropertyName("transaction_id")] public string? TransactionId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
