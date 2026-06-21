// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.BricsPay.Configuration;
using Bhengu.Finance.Payments.BricsPay.Internals;
using Bhengu.Finance.Payments.BricsPay.Models;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.QrCode;
using Bhengu.Finance.Payments.Core.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.BricsPay.Providers;

/// <summary>
/// BRICS Pay e-commerce acquiring — "Internet Acquiring" QR payments on the Joys processing platform.
/// The customer pays by scanning a QR on a hosted payment page; there is no card token.
/// <para>
/// This provider creates a transaction (<c>POST /ia/api</c> → hosted payment-page URL containing the QR),
/// polls status (<c>GET /ia/get</c>), refunds (<c>POST /ia/refund</c>) and parses callbacks. Every request
/// is signed with the merchant's private key (ECDSA/RSA); BRICS Pay verifies with the public key registered
/// at onboarding. See <c>BRICS_PAY_API_REFERENCE.md</c>.
/// </para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Built from BRICS Pay's published E-Commerce protocol; never verified against a live terminal (onboarding required).")]
public sealed class BricsPayPaymentProvider : BhenguProviderBase, IQrCodeProvider
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly BricsPayOptions _options;
    private readonly string _baseUrl;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.BricsPay;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public BricsPayPaymentProvider(
        HttpClient httpClient,
        IOptions<BricsPayOptions> options,
        ILogger<BricsPayPaymentProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.TerminalId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.TerminalId)} is required");
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.BaseUrl)} is required (provisioned per terminal at onboarding)");
        if (string.IsNullOrWhiteSpace(_options.PrivateKeyPem))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(BricsPayOptions.PrivateKeyPem)} is required to sign requests");

        _baseUrl = _options.BaseUrl.TrimEnd('/');
    }

    /// <inheritdoc/>
    public Task<QrCode> GenerateQrAsync(QrCodeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("generate_qr", () => GenerateQrCoreAsync(request, ct), ct);
    }

    private async Task<QrCode> GenerateQrCoreAsync(QrCodeRequest request, CancellationToken ct)
    {
        if (request.Amount is not { } amount)
            throw new BhenguPaymentException(ProviderName,
                "BRICS Pay requires a fixed amount — amount-less (static) QR codes are not supported by the e-commerce protocol.");
        if (string.IsNullOrWhiteSpace(request.PayerIdentifier))
            throw new BhenguPaymentException(ProviderName,
                "BRICS Pay requires the buyer's SHA-256(IP + User-Agent) supplied via QrCodeRequest.PayerIdentifier (the 'User' field).");

        var requestBody = new CreateTransactionBody
        {
            Pos = _options.TerminalId,
            Sequence = request.MerchantReference,
            Amount = amount.ToString(CultureInfo.InvariantCulture),
            User = request.PayerIdentifier,
            Callback = _options.CallbackUrl,
            CSS = _options.CssUrl,
            Return = _options.ReturnUrl,
            TTL = ResolveTtl(request.ExpiresAt)
        };

        var json = JsonSerializer.Serialize(requestBody, s_json);
        var url = $"{_baseUrl}/ia/api/?signature={Uri.EscapeDataString(BricsPaySigner.Sign(json, _options))}";

        var responseBody = await SendAsync(HttpMethod.Post, url, json, ct, "GenerateQr").ConfigureAwait(false);
        var created = JsonSerializer.Deserialize<CreateTransactionResponse>(responseBody, s_json);

        if (string.IsNullOrWhiteSpace(created?.URL))
            throw new BhenguPaymentException(ProviderName, "BRICS Pay did not return a payment-page URL.");

        Logger.LogInformation("BRICS Pay transaction created: sequence={Sequence} amount={Amount} {Currency}",
            request.MerchantReference, amount, request.Currency);

        return new QrCode
        {
            Reference = request.MerchantReference,
            Format = QrFormat.Payload,
            Payload = created.URL,
            Amount = amount,
            Currency = request.Currency,
            ExpiresAt = request.ExpiresAt
        };
    }

    /// <inheritdoc/>
    public Task<PaymentStatus> GetQrStatusAsync(string qrReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(qrReference);
        return RunOperationAsync("get_qr_status", async () =>
        {
            var status = await FetchStatusAsync(qrReference, ct).ConfigureAwait(false);
            return status.Status;
        }, ct);
    }

    /// <summary>
    /// Fetch the full transaction status for a previously-created QR, keyed by its <c>Sequence</c>
    /// (the <see cref="QrCodeRequest.MerchantReference"/> you passed to <see cref="GenerateQrAsync"/>).
    /// </summary>
    public Task<BricsPayTransactionStatus> GetTransactionAsync(string sequence, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequence);
        return RunOperationAsync("get_transaction", () => FetchStatusAsync(sequence, ct), ct);
    }

    private async Task<BricsPayTransactionStatus> FetchStatusAsync(string sequence, CancellationToken ct)
    {
        // The signed message for a GET is the query parameters as they appear in the URL.
        var query = $"pos={Uri.EscapeDataString(_options.TerminalId)}&sequence={Uri.EscapeDataString(sequence)}";
        var url = $"{_baseUrl}/ia/get/?{query}&signature={Uri.EscapeDataString(BricsPaySigner.Sign(query, _options))}";

        var body = await SendAsync(HttpMethod.Get, url, null, ct, "GetTransaction").ConfigureAwait(false);
        var dto = JsonSerializer.Deserialize<TransactionStatusResponse>(body, s_json)
                  ?? throw new BhenguPaymentException(ProviderName, "BRICS Pay returned an unparseable status response.");
        return MapStatus(dto);
    }

    /// <summary>
    /// Full or partial refund of a settled BRICS Pay transaction.
    /// </summary>
    /// <param name="originalTransaction">The <c>Transaction</c> number returned by the original payment.</param>
    /// <param name="refundSequence">A NEW unique per-terminal operation number for this refund (not the original's Sequence).</param>
    /// <param name="amount">Refund amount; must not exceed the original.</param>
    public Task<BricsPayTransactionStatus> RefundAsync(string originalTransaction, string refundSequence, decimal amount, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(originalTransaction);
        ArgumentException.ThrowIfNullOrEmpty(refundSequence);
        return RunOperationAsync("refund", async () =>
        {
            var requestBody = new RefundBody
            {
                POS = _options.TerminalId,
                Sequence = refundSequence,
                Reference = originalTransaction,
                Amount = amount.ToString(CultureInfo.InvariantCulture)
            };

            var json = JsonSerializer.Serialize(requestBody, s_json);
            var url = $"{_baseUrl}/ia/refund?signature={Uri.EscapeDataString(BricsPaySigner.Sign(json, _options))}";

            var responseBody = await SendAsync(HttpMethod.Post, url, json, ct, "Refund").ConfigureAwait(false);
            var dto = JsonSerializer.Deserialize<TransactionStatusResponse>(responseBody, s_json)
                      ?? throw new BhenguPaymentException(ProviderName, "BRICS Pay returned an unparseable refund response.");
            return MapStatus(dto);
        }, ct);
    }

    /// <summary>
    /// Parse a BRICS Pay callback body. The captured protocol does not specify callback signing, so treat
    /// the result as a signal to confirm the authoritative state via <see cref="GetTransactionAsync"/> —
    /// do not trust these fields on their own. Returns null if the payload cannot be parsed.
    /// </summary>
    public BricsPayCallback? ParseCallback(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        try
        {
            var dto = JsonSerializer.Deserialize<CallbackBody>(payload, s_json);
            if (dto is null) return null;

            return new BricsPayCallback
            {
                Pos = dto.POS,
                Sequence = dto.Sequence,
                Transaction = dto.Transaction,
                Paid = dto.Paid,
                Processed = dto.Processed,
                Status = MapPaymentStatus(dto.Paid, dto.Processed),
                Amount = ParseAmount(dto.Amount),
                CurrencyCode = dto.Currency?.Code ?? 0
            };
        }
        catch (JsonException ex)
        {
            Logger.LogError(ex, "Failed to parse BRICS Pay callback payload");
            return null;
        }
    }

    private byte? ResolveTtl(DateTime? expiresAt)
    {
        if (expiresAt is { } exp)
        {
            var minutes = (int)Math.Ceiling((exp - DateTime.UtcNow).TotalMinutes);
            if (minutes < 1) minutes = 1;
            if (minutes > 255) minutes = 255;
            return (byte)minutes;
        }
        return _options.DefaultTtlMinutes;
    }

    private async Task<string> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, url);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "BRICS Pay request failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("BRICS Pay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            // 401 = signature rejected; other 4xx = request-level decline; 5xx/unknown = unavailable.
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static BricsPayTransactionStatus MapStatus(TransactionStatusResponse dto) => new()
    {
        Transaction = dto.Transaction,
        Paid = dto.Paid,
        Processed = dto.Processed,
        Status = MapPaymentStatus(dto.Paid, dto.Processed),
        Amount = ParseAmount(dto.Amount),
        CurrencyCode = dto.Currency?.Code ?? 0,
        CurrencyName = dto.Currency?.Name,
        CreatedUtc = ParseDate(dto.Time?.Created),
        ProcessedUtc = ParseDate(dto.Time?.Processed),
        TimeoutUtc = ParseDate(dto.Time?.Timeout),
        ErrorCode = dto.Error?.Code,
        ErrorMessage = dto.Error?.Message
    };

    private static PaymentStatus MapPaymentStatus(bool paid, bool processed) => (paid, processed) switch
    {
        (true, true) => PaymentStatus.Completed,
        (false, true) => PaymentStatus.Failed,
        _ => PaymentStatus.Pending
    };

    private static decimal ParseAmount(string? raw) =>
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static DateTime? ParseDate(string? raw) =>
        DateTime.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var v) ? v : null;

    // ---- wire models (BRICS Pay uses PascalCase JSON keys) ----

    private sealed record CreateTransactionBody
    {
        public string? Pos { get; init; }
        public string? Sequence { get; init; }
        public string? Amount { get; init; }
        public string? User { get; init; }
        public string? Callback { get; init; }
        public string? CSS { get; init; }
        public string? Return { get; init; }
        public byte? TTL { get; init; }
    }

    private sealed record CreateTransactionResponse
    {
        public string? URL { get; init; }
    }

    private sealed record RefundBody
    {
        public string? POS { get; init; }
        public string? Sequence { get; init; }
        public string? Reference { get; init; }
        public string? Amount { get; init; }
    }

    private sealed record TransactionStatusResponse
    {
        public string? Transaction { get; init; }
        public bool Paid { get; init; }
        public bool Processed { get; init; }
        public string? Amount { get; init; }
        public CurrencyDto? Currency { get; init; }
        public TimeDto? Time { get; init; }
        public string? Reference { get; init; }
        public ErrorDto? Error { get; init; }
    }

    private sealed record CallbackBody
    {
        public string? POS { get; init; }
        public string? Sequence { get; init; }
        public string? Transaction { get; init; }
        public bool Paid { get; init; }
        public bool Processed { get; init; }
        public string? Amount { get; init; }
        public CurrencyDto? Currency { get; init; }
    }

    private sealed record CurrencyDto
    {
        public int Code { get; init; }
        public int Precision { get; init; }
        public string? Name { get; init; }
        public string? Symbol { get; init; }
    }

    private sealed record TimeDto
    {
        public string? Created { get; init; }
        public string? Processed { get; init; }
        public string? Timeout { get; init; }
    }

    private sealed record ErrorDto
    {
        public string? Code { get; init; }
        public string? Message { get; init; }
    }
}
