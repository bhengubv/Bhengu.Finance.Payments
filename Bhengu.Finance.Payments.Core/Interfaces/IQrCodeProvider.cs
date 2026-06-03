// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;

namespace Bhengu.Finance.Payments.Core.Interfaces;

/// <summary>
/// Optional contract for providers that can generate payment QR codes.
/// Implemented by Alipay (pre-create QR), WeChat Pay (Native), PayShap (PayShap QR), MercadoPago
/// (PIX QR), PagSeguro (PIX QR) etc.
/// </summary>
public interface IQrCodeProvider
{
    /// <summary>The provider this QR capability belongs to.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Generate a QR. When <see cref="QrCodeRequest.Amount"/> is set the QR is dynamic (locked amount,
    /// usually short-lived); when null the QR is static (payer enters the amount).
    /// </summary>
    /// <exception cref="Exceptions.ProviderUnavailableException">Provider was unreachable.</exception>
    /// <exception cref="Exceptions.BhenguPaymentException">Provider rejected the request.</exception>
    Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default);

    /// <summary>
    /// Poll the payment status of a previously-generated QR. Use this if you don't have webhook delivery
    /// set up; otherwise rely on the parent provider's webhook to learn of payment completion.
    /// </summary>
    /// <returns>Current <see cref="PaymentStatus"/>; <see cref="PaymentStatus.Pending"/> while the QR hasn't been paid.</returns>
    Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default);
}
