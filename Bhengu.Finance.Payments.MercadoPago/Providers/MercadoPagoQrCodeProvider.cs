// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MercadoPago.Providers;

/// <summary>
/// Mercado Pago PIX QR code provider. Generates Brazilian PIX EMVCo BR Code payloads via
/// <c>POST /v1/payments</c> with <c>payment_method_id=pix</c>; the response carries the
/// QR text plus a base64-encoded PNG under <c>point_of_interaction.transaction_data</c>.
/// </summary>
/// <remarks>
/// PIX is Brazil's instant payments scheme. Mercado Pago issues a dynamic PIX QR per charge —
/// the QR locks the amount and expires (default ~30 minutes). The QR payload is the standard
/// EMVCo BR Code "copy-paste" string; the PNG is what most apps actually render. SVG is NOT
/// supported by Mercado Pago — use <see cref="QrFormat.Payload"/> and render the QR yourself
/// (e.g. via QRCoder), or request <see cref="QrFormat.Png"/> to get Mercado Pago's pre-rendered
/// PNG bytes.
/// </remarks>
public sealed class MercadoPagoQrCodeProvider : BhenguProviderBase, IQrCodeProvider
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly MercadoPagoOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.MercadoPago;

    /// <summary>Create a new Mercado Pago PIX QR provider bound to the supplied HTTP client and options.</summary>
    public MercadoPagoQrCodeProvider(
        HttpClient httpClient,
        IOptions<MercadoPagoOptions> options,
        ILogger<MercadoPagoQrCodeProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MercadoPagoOptions.AccessToken)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mercadopago.com");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    /// <inheritdoc />
    public async Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Format == QrFormat.Svg)
            throw new BhenguPaymentException(
                ProviderName,
                "SVG not supported by Mercado Pago; use Payload or Png");

        var payerEmail = request.PayerIdentifier ?? "pix-payer@anonymous";

        var body = new Dictionary<string, object?>
        {
            ["transaction_amount"] = request.Amount ?? 0m,
            ["description"] = request.Description,
            ["payment_method_id"] = "pix",
            ["external_reference"] = request.MerchantReference,
            ["notification_url"] = _options.NotificationUrl,
            ["date_of_expiration"] = request.ExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", System.Globalization.CultureInfo.InvariantCulture),
            ["payer"] = new Dictionary<string, object?>
            {
                ["email"] = payerEmail
            }
        };

        var raw = await SendAsync(HttpMethod.Post, "/v1/payments", body, ct, "GenerateQr", request.IdempotencyKey).ConfigureAwait(false);
        var payment = DeserialiseOrThrow<MercadoPagoPixPayment>(raw, "GenerateQr");

        var qrText = payment.PointOfInteraction?.TransactionData?.QrCode;
        var qrPngBase64 = payment.PointOfInteraction?.TransactionData?.QrCodeBase64;

        if (string.IsNullOrEmpty(qrText))
            throw new BhenguPaymentException(ProviderName, "Mercado Pago PIX response did not contain a QR payload");

        Logger.LogInformation(
            "Mercado Pago PIX QR generated: paymentId={Id} reference={Ref} amount={Amount}",
            payment.Id,
            request.MerchantReference,
            request.Amount);

        byte[]? bytes = null;
        if (request.Format == QrFormat.Png)
        {
            if (string.IsNullOrEmpty(qrPngBase64))
                throw new BhenguPaymentException(ProviderName, "Mercado Pago PIX response did not contain a base64 PNG");
            try
            {
                bytes = Convert.FromBase64String(qrPngBase64);
            }
            catch (FormatException ex)
            {
                throw new BhenguPaymentException(ProviderName, "Mercado Pago returned a malformed base64 PNG", innerException: ex);
            }
        }

        return new QrCode
        {
            Reference = payment.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Format = request.Format,
            Payload = request.Format == QrFormat.Payload ? qrText : null,
            ImageBytes = bytes,
            Amount = request.Amount,
            Currency = payment.CurrencyId ?? request.Currency,
            ExpiresAt = request.ExpiresAt ?? TryParseDate(payment.DateOfExpiration)
        };
    }

    /// <inheritdoc />
    public async Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrReference);

        var raw = await SendAsync(HttpMethod.Get, $"/v1/payments/{Uri.EscapeDataString(qrReference)}", body: null, ct, "GetQrStatus", idempotencyKey: null).ConfigureAwait(false);
        var payment = DeserialiseOrThrow<MercadoPagoPixPayment>(raw, "GetQrStatus");
        return MapStatus(payment.Status);
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "approved" => PaymentStatus.Completed,
        "pending" or "in_process" or "in_mediation" => PaymentStatus.Pending,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "rejected" or "failed" => PaymentStatus.Failed,
        "refunded" or "charged_back" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private static DateTime? TryParseDate(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : (DateTime?)null;

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation, string? idempotencyKey)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (method == HttpMethod.Post)
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Mercado Pago failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Mercado Pago {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static T DeserialiseOrThrow<T>(string raw, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw)
                ?? throw new BhenguPaymentException(ProviderNames.MercadoPago, $"Mercado Pago {operation} returned empty body");
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(ProviderNames.MercadoPago, $"Failed to parse Mercado Pago {operation} response", innerException: ex);
        }
    }

    // === Mercado Pago API response shapes (internal) ===

    private sealed class MercadoPagoPixPayment
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
        [JsonPropertyName("currency_id")] public string? CurrencyId { get; set; }
        [JsonPropertyName("transaction_amount")] public decimal? TransactionAmount { get; set; }
        [JsonPropertyName("date_of_expiration")] public string? DateOfExpiration { get; set; }
        [JsonPropertyName("point_of_interaction")] public MercadoPagoPointOfInteraction? PointOfInteraction { get; set; }
    }

    private sealed class MercadoPagoPointOfInteraction
    {
        [JsonPropertyName("transaction_data")] public MercadoPagoPixTransactionData? TransactionData { get; set; }
    }

    private sealed class MercadoPagoPixTransactionData
    {
        [JsonPropertyName("qr_code")] public string? QrCode { get; set; }
        [JsonPropertyName("qr_code_base64")] public string? QrCodeBase64 { get; set; }
        [JsonPropertyName("ticket_url")] public string? TicketUrl { get; set; }
    }
}
