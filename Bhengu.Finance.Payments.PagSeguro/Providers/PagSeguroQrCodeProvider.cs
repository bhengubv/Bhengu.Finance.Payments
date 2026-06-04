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
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PagSeguro.Providers;

/// <summary>
/// PagSeguro / PagBank PIX QR code provider. Generates Brazilian PIX EMVCo BR Code payloads via
/// <c>POST /orders</c> with <c>charges[].payment_method.type = "PIX"</c>. The response carries
/// the QR text under <c>qr_codes[0].text</c> plus a PNG image URL under
/// <c>qr_codes[0].links[].href</c>.
/// </summary>
/// <remarks>
/// PagBank issues a dynamic PIX QR per order. The QR locks the amount and expires according to the
/// caller-supplied <see cref="QrCodeRequest.ExpiresAt"/> (defaults to ~30 minutes when omitted).
/// SVG is NOT supported by PagBank — request <see cref="QrFormat.Payload"/> and render the QR
/// yourself, or request <see cref="QrFormat.Png"/> to have this provider download the PNG bytes
/// from the URL the order response returned.
/// </remarks>
public sealed class PagSeguroQrCodeProvider : BhenguProviderBase, IQrCodeProvider
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly PagSeguroOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.PagSeguro;

    /// <summary>Create a new PagSeguro PIX QR provider bound to the supplied HTTP client and options.</summary>
    public PagSeguroQrCodeProvider(
        HttpClient httpClient,
        IOptions<PagSeguroOptions> options,
        ILogger<PagSeguroQrCodeProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PagSeguroOptions.ApiToken)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? (_options.SandboxUrl ?? "https://sandbox.api.pagseguro.com")
                : (_options.BaseUrl ?? "https://api.pagseguro.com");
            _httpClient.BaseAddress = new Uri(resolved);
        }

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
    }

    /// <inheritdoc />
    public async Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Format == QrFormat.Svg)
            throw new BhenguPaymentException(
                ProviderName,
                "SVG not supported by PagSeguro; use Payload or Png");

        var amountInCents = (long)((request.Amount ?? 0m) * 100);
        var currency = (request.Currency ?? _options.Currency).ToUpperInvariant();
        var expiresAt = request.ExpiresAt ?? DateTime.UtcNow.AddMinutes(30);

        var body = new Dictionary<string, object?>
        {
            ["reference_id"] = request.MerchantReference,
            ["customer"] = new Dictionary<string, object?>
            {
                ["email"] = request.PayerIdentifier
            },
            ["qr_codes"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["amount"] = new { value = amountInCents, currency },
                    ["expiration_date"] = expiresAt.ToString("yyyy-MM-ddTHH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture)
                }
            },
            ["notification_urls"] = _options.NotificationUrl is null ? null : new[] { _options.NotificationUrl }
        };

        var raw = await SendAsync(HttpMethod.Post, "/orders", body, ct, "GenerateQr").ConfigureAwait(false);
        var order = DeserialiseOrThrow<PagSeguroPixOrder>(raw, "GenerateQr");

        var qr = order.QrCodes?.FirstOrDefault()
            ?? throw new BhenguPaymentException(ProviderName, "PagSeguro PIX response did not contain a qr_codes block");

        if (string.IsNullOrEmpty(qr.Text))
            throw new BhenguPaymentException(ProviderName, "PagSeguro PIX response did not contain a QR text payload");

        Logger.LogInformation("PagSeguro PIX QR generated: orderId={Id} reference={Ref} amount={Amount}",
            order.Id, request.MerchantReference, request.Amount);

        byte[]? bytes = null;
        if (request.Format == QrFormat.Png)
        {
            var pngUrl = qr.Links?.FirstOrDefault(l => string.Equals(l.Media, "image/png", StringComparison.OrdinalIgnoreCase))?.Href
                ?? qr.Links?.FirstOrDefault()?.Href;
            if (string.IsNullOrEmpty(pngUrl))
                throw new BhenguPaymentException(ProviderName, "PagSeguro PIX response did not contain a PNG link");
            bytes = await DownloadBytesAsync(pngUrl, ct).ConfigureAwait(false);
        }

        return new QrCode
        {
            Reference = order.Id ?? string.Empty,
            Format = request.Format,
            Payload = request.Format == QrFormat.Payload ? qr.Text : null,
            ImageBytes = bytes,
            Amount = request.Amount,
            Currency = qr.Amount?.Currency ?? currency,
            ExpiresAt = expiresAt
        };
    }

    /// <inheritdoc />
    public async Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrReference);

        var raw = await SendAsync(HttpMethod.Get, $"/orders/{Uri.EscapeDataString(qrReference)}", body: null, ct, "GetQrStatus").ConfigureAwait(false);
        var order = DeserialiseOrThrow<PagSeguroPixOrder>(raw, "GetQrStatus");

        // PagBank reports the PIX status on the first charge once a payer scans the QR.
        var firstChargeStatus = order.Charges?.FirstOrDefault()?.Status ?? order.Status;
        return MapStatus(firstChargeStatus);
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "PAID" or "AUTHORIZED" or "CAPTURED" or "COMPLETED" => PaymentStatus.Completed,
        "WAITING" or "IN_ANALYSIS" or "PENDING" or "PROCESSING" => PaymentStatus.Pending,
        "DECLINED" or "FAILED" => PaymentStatus.Failed,
        "CANCELED" or "CANCELLED" or "VOIDED" or "EXPIRED" => PaymentStatus.Cancelled,
        "REFUNDED" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Don't echo the bearer token to arbitrary image hosts — issue without auth.
            using var client = new HttpClient();
            using var response = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new BhenguPaymentException(ProviderName, $"PagSeguro PNG download failed: HTTP {(int)response.StatusCode}");
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "PagSeguro PNG download failed", ex);
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PagSeguro failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PagSeguro {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
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
                ?? throw new BhenguPaymentException(ProviderNames.PagSeguro, $"PagSeguro {operation} returned empty body");
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(ProviderNames.PagSeguro, $"Failed to parse PagSeguro {operation} response", innerException: ex);
        }
    }

    // === PagSeguro API response shapes (internal) ===

    private sealed class PagSeguroPixOrder
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("reference_id")] public string? ReferenceId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("qr_codes")] public PagSeguroQrCodeEntry[]? QrCodes { get; set; }
        [JsonPropertyName("charges")] public PagSeguroPixCharge[]? Charges { get; set; }
    }

    private sealed class PagSeguroQrCodeEntry
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("expiration_date")] public string? ExpirationDate { get; set; }
        [JsonPropertyName("amount")] public PagSeguroQrAmount? Amount { get; set; }
        [JsonPropertyName("links")] public PagSeguroLink[]? Links { get; set; }
    }

    private sealed class PagSeguroQrAmount
    {
        [JsonPropertyName("value")] public long Value { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class PagSeguroLink
    {
        [JsonPropertyName("rel")] public string? Rel { get; set; }
        [JsonPropertyName("href")] public string? Href { get; set; }
        [JsonPropertyName("media")] public string? Media { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class PagSeguroPixCharge
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
