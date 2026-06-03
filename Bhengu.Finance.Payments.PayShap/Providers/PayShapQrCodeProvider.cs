// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Text;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.PayShap.Configuration;
using Bhengu.Finance.Payments.PayShap.Models.Requests;
using Bhengu.Finance.Payments.PayShap.Services.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayShap.Providers;

/// <summary>
/// PayShap QR code provider — issues PayShap deep-link payloads that a payer's banking
/// wallet recognises and uses to address an RTC credit transfer to the merchant.
/// <para>
/// The QR payload conforms to the PayShap deep-link convention:
/// <c>payshap://pay?bankcode=...&amp;account=...&amp;amount=...&amp;ref=...&amp;payeename=...</c>.
/// Static QRs omit the <c>amount</c> parameter so the payer enters it in their wallet.
/// </para>
/// <para>
/// The merchant identity (bank code, account, display name) is taken from
/// <see cref="PayShapSettings.Payee"/>. PNG and SVG output are NOT supported by this
/// provider — request <see cref="QrFormat.Payload"/> and render the QR yourself using a
/// library such as QRCoder. This keeps the SDK dependency-free.
/// </para>
/// <para>
/// Status polling delegates to the parent <see cref="IPayShapService.GetPaymentStatusAsync"/>
/// endpoint; PayShap also delivers status via webhook, so webhook is the preferred channel.
/// </para>
/// </summary>
public sealed class PayShapQrCodeProvider : IQrCodeProvider
{
    private readonly IPayShapService _payShapService;
    private readonly PayShapSettings _settings;
    private readonly ILogger<PayShapQrCodeProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.PayShap;

    /// <summary>Construct a PayShap QR provider.</summary>
    public PayShapQrCodeProvider(
        IPayShapService payShapService,
        IOptions<PayShapSettings> settings,
        ILogger<PayShapQrCodeProvider> logger)
    {
        _payShapService = payShapService ?? throw new ArgumentNullException(nameof(payShapService));
        _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Format != QrFormat.Payload)
        {
            throw new BhenguPaymentException(
                ProviderName,
                $"QrFormat.{request.Format} not supported by this provider. Use QrFormat.Payload and render the payload yourself with QRCoder or similar.");
        }

        var payee = _settings.Payee
            ?? throw new ProviderConfigurationException(
                ProviderName,
                $"{nameof(PayShapSettings)}.{nameof(PayShapSettings.Payee)} must be configured to generate PayShap QR codes.");

        if (string.IsNullOrWhiteSpace(payee.BankCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayShapPayeeSettings.BankCode)} is required for PayShap QR codes.");
        if (string.IsNullOrWhiteSpace(payee.Account))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayShapPayeeSettings.Account)} is required for PayShap QR codes.");
        if (string.IsNullOrWhiteSpace(payee.Name))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayShapPayeeSettings.Name)} is required for PayShap QR codes.");

        var parts = new List<string>(8)
        {
            $"bankcode={Uri.EscapeDataString(payee.BankCode)}",
            $"account={Uri.EscapeDataString(payee.Account)}",
            $"payeename={Uri.EscapeDataString(payee.Name)}",
            $"ref={Uri.EscapeDataString(request.MerchantReference)}"
        };

        if (request.Amount is { } amt)
        {
            parts.Add($"amount={amt.ToString("F2", CultureInfo.InvariantCulture)}");
            parts.Add($"currency={Uri.EscapeDataString(request.Currency)}");
        }

        if (!string.IsNullOrWhiteSpace(payee.IdentifierType))
            parts.Add($"identifiertype={Uri.EscapeDataString(payee.IdentifierType)}");
        if (!string.IsNullOrWhiteSpace(payee.IdentifierValue))
            parts.Add($"identifier={Uri.EscapeDataString(payee.IdentifierValue)}");

        if (!string.IsNullOrWhiteSpace(request.Description))
            parts.Add($"desc={Uri.EscapeDataString(request.Description)}");

        var sb = new StringBuilder("payshap://pay?");
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(parts[i]);
        }

        var payload = sb.ToString();
        _logger.LogInformation(
            "PayShap QR generated: ref={Reference} amount={Amount} static={IsStatic}",
            request.MerchantReference,
            request.Amount,
            request.Amount is null);

        var qr = new QrCode
        {
            Reference = request.MerchantReference,
            Format = QrFormat.Payload,
            Payload = payload,
            Amount = request.Amount,
            Currency = request.Currency,
            ExpiresAt = request.ExpiresAt
        };

        return Task.FromResult(qr);
    }

    /// <inheritdoc />
    public async Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrReference);

        var statusRequest = new PaymentStatusRequest
        {
            PaymentId = qrReference,
            MerchantId = _settings.MerchantId
        };

        try
        {
            var status = await _payShapService.GetPaymentStatusAsync(statusRequest).ConfigureAwait(false);
            return MapStatus(status.Status);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "PayShap status call failed", ex);
        }
        catch (Exception ex) when (ex is not BhenguPaymentException)
        {
            throw new BhenguPaymentException(
                ProviderName,
                "PayShap status lookup failed",
                providerErrorMessage: ex.Message,
                innerException: ex);
        }
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToUpperInvariant() switch
    {
        "COMPLETED" or "SUCCESS" or "SETTLED" => PaymentStatus.Completed,
        "PENDING" or "PROCESSING" or "ACCEPTED" => PaymentStatus.Pending,
        "FAILED" or "REJECTED" or "ERROR" => PaymentStatus.Failed,
        "CANCELLED" or "CANCELED" or "EXPIRED" => PaymentStatus.Cancelled,
        "REVERSED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };
}
