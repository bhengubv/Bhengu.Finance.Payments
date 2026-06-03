// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Wave.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Wave.Providers;

/// <summary>
/// Standalone Wave Mobile Money <see cref="IPayoutProvider"/> implementation that wraps the
/// Wave Business <c>v1/payout</c> endpoint. Distinct from <see cref="WavePaymentProvider"/>'s
/// own <see cref="IPayoutProvider"/> surface so consumers can register the disbursement pipeline
/// independently.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotency:</b> Wave natively supports <c>idempotency_key</c> in the payout body — the same
/// caller-supplied key collapses retries to the same payout server-side. When
/// <see cref="PayoutRequest.IdempotencyKey"/> is missing a fresh GUID is minted per request.
/// </para>
/// <para>
/// <b>MSISDN format:</b> <see cref="PayoutRequest.DestinationToken"/> may be either the raw
/// national phone number (defaults to country <c>SN</c>) or a colon-prefixed
/// <c>&lt;country&gt;:&lt;phone&gt;</c> tuple (e.g. <c>SN:221761234567</c>, <c>CI:0701234567</c>).
/// Wave returns HTTP 400 with code <c>recipient-not-found</c> for unrecognised numbers.
/// </para>
/// <para>
/// <b>Amount format:</b> Wave amounts are integer strings in the major unit (XOF/XAF have no
/// minor unit). Sub-unit amounts are silently truncated server-side, which can cause silent
/// under-payment; callers should round upstream.
/// </para>
/// </remarks>
public sealed class WavePayoutProvider : IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly WaveOptions _options;
    private readonly ILogger<WavePayoutProvider> _logger;

    /// <inheritdoc/>
    public string ProviderName => ProviderNames.Wave;

    /// <summary>Construct a standalone Wave payout provider. Designed to be registered via DI.</summary>
    public WavePayoutProvider(
        HttpClient httpClient,
        IOptions<WaveOptions> options,
        ILogger<WavePayoutProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(WaveOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.wave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    /// <inheritdoc/>
    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.DestinationToken))
            throw new PaymentDeclinedException(ProviderName, "invalid_msisdn",
                "Wave Payout requires the recipient MSISDN in PayoutRequest.DestinationToken.");

        // DestinationToken format: "<countryCode>:<phone>" (e.g. "SN:221761234567") OR raw phone.
        var countryCode = "SN";
        var phone = request.DestinationToken;
        var colon = request.DestinationToken.IndexOf(':');
        if (colon > 0)
        {
            countryCode = request.DestinationToken[..colon];
            phone = request.DestinationToken[(colon + 1)..];
        }

        var idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
            ? $"payout-{Guid.NewGuid():N}"
            : request.IdempotencyKey!;

        var body = new
        {
            receive_amount = request.Amount.ToString("F0", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            mobile = new
            {
                national_id = phone,
                country_code = countryCode
            },
            name = request.Description,
            payment_reason = request.Description,
            idempotency_key = idempotencyKey,
            client_reference = idempotencyKey
        };

        var responseBody = await SendAsync(HttpMethod.Post, "v1/payout", body, ct, "ProcessPayout").ConfigureAwait(false);
        var payoutResponse = JsonSerializer.Deserialize<WavePayoutResponse>(responseBody);

        _logger.LogInformation(
            "Wave payout created: Id={Id} Status={Status} IdempotencyKey={IdempotencyKey}",
            payoutResponse?.Id, payoutResponse?.Status, idempotencyKey);

        return new PayoutResponse
        {
            GatewayReference = payoutResponse?.Id ?? string.Empty,
            Status = MapStatus(payoutResponse?.Status ?? "pending"),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, $"HTTP request to Wave ({operation}) failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Wave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "complete" or "completed" or "succeeded" or "successful" or "processing_successful" => PaymentStatus.Completed,
        "open" or "pending" or "processing" => PaymentStatus.Pending,
        "failed" or "expired" or "processing_failed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    private sealed class WavePayoutResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("receive_amount")] public string? ReceiveAmount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }
}
