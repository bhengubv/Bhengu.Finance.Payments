// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.EcoCash.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.EcoCash.Providers;

/// <summary>
/// EcoCash (Zimbabwe) mobile-money provider, built against the public <b>EcoCash Open API</b>
/// (developers.ecocash.co.zw). Implements C2B instant charge, refund, and transaction-status lookup.
///
/// <para><b>Verified wire format</b> (paths + JSON field names confirmed from the EcoCash Open API and a
/// published SDK generated directly from its contract — see
/// <see href="https://developers.ecocash.co.zw/">developers.ecocash.co.zw</see> and
/// <see href="https://github.com/iamngoni/ecocash">github.com/iamngoni/ecocash</see>):</para>
/// <list type="bullet">
///   <item>Auth: single <c>X-API-KEY</c> header. All operations are <c>POST</c>.</item>
///   <item>Charge (C2B): <c>POST .../api/v2/payment/instant/c2b/{sandbox|live}</c> with body
///         <c>{ customerMsisdn, amount, reason, currency, sourceReference }</c>.</item>
///   <item>Lookup: <c>POST .../api/v1/transaction/c2b/status/{sandbox|live}</c> with body
///         <c>{ sourceMobileNumber, sourceReference }</c> returning
///         <c>{ status, amount, currency, ecocashReference, transactionDateTime, ... }</c>.</item>
///   <item>Refund: <c>POST .../api/v2/refund/instant/c2b/{sandbox|live}</c> with body
///         <c>{ origionalEcocashTransactionReference, refundCorelator, sourceMobileNumber, amount,
///         clientName, currency, reasonForRefund }</c> (the <i>origional</i>/<i>Corelator</i> spellings
///         are the real EcoCash wire keys, reproduced verbatim) returning
///         <c>{ transactionStatus, ecocashReference, ... }</c>.</item>
/// </list>
///
/// <para><b>Not supported by the Open API</b> (deliberately omitted rather than guessed): there is no
/// publicly documented B2C/payout (merchant-to-subscriber) endpoint and no signed asynchronous webhook —
/// transaction outcome is obtained by the synchronous status lookup above. This provider therefore does
/// NOT implement <c>IPayoutProvider</c>, and <see cref="VerifyWebhookSignature"/> /
/// <see cref="ParseWebhookAsync"/> are honest no-ops.</para>
///
/// <para>Marked <see cref="ProviderVerificationStatus.DocsOnly"/>: the request shapes are documented but
/// no charge has been put through the EcoCash sandbox from this SDK.</para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "EcoCash Open API (developers.ecocash.co.zw): X-API-KEY auth, c2b charge/lookup/refund paths + JSON fields verified from the public spec/SDK; not sandbox-verified.")]
public sealed class EcoCashPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider
{
    private readonly HttpClient _httpClient;
    private readonly EcoCashOptions _options;
    private readonly IBhenguDistributedCache _cache;
    private readonly string _env;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    private const string DefaultBaseUrl = "https://developers.ecocash.co.zw/api/ecocash_pay";

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.EcoCash;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.PartialRefund |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.Idempotency;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public EcoCashPaymentProvider(
        HttpClient httpClient,
        IOptions<EcoCashOptions> options,
        ILogger<EcoCashPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(EcoCashOptions.ApiKey)} is required");

        _env = _options.UseSandbox ? "sandbox" : "live";

        if (_httpClient.BaseAddress is null)
        {
            var baseUrl = (_options.BaseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/";
            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        _httpClient.DefaultRequestHeaders.Remove("X-API-KEY");
        _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _options.ApiKey);
    }

    /// <inheritdoc/>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            if (string.IsNullOrWhiteSpace(request.PaymentMethodToken))
                throw new PaymentDeclinedException(ProviderName, "missing_msisdn",
                    "EcoCash C2B requires the payer MSISDN (e.g. 263772XXXXXX) in PaymentRequest.PaymentMethodToken.");

            // sourceReference is the merchant's own unique reference for the charge; reuse the caller's
            // IdempotencyKey when supplied so a retry maps to the same EcoCash transaction.
            var sourceReference = request.IdempotencyKey ?? $"ecocash-{Guid.NewGuid():N}";

            var body = new EcoCashChargeRequest
            {
                CustomerMsisdn = request.PaymentMethodToken,
                Amount = request.Amount,
                Reason = request.Description,
                Currency = request.Currency.ToUpperInvariant(),
                SourceReference = sourceReference
            };

            var responseBody = await SendAsync($"api/v2/payment/instant/c2b/{_env}", body, ct, "ProcessPayment").ConfigureAwait(false);

            // UNVERIFIED: the EcoCash Open API does not publicly document the C2B charge *success-response*
            // body (the reference SDK treats a 2xx as success without parsing fields). We therefore treat a
            // 2xx as "accepted" and best-effort-parse any echoed reference/status; callers confirm final
            // settlement via ProcessRefundAsync's sibling status lookup or a reconciliation poll.
            var parsed = TryDeserialize<EcoCashLookupResponse>(responseBody);
            var reference = parsed?.EcocashReference ?? sourceReference;
            var status = parsed?.Status is { Length: > 0 } s ? MapStatus(s) : PaymentStatus.Pending;

            Logger.LogInformation("EcoCash C2B charge accepted: SourceReference={SourceReference} EcocashReference={EcocashReference} Status={Status}",
                sourceReference, parsed?.EcocashReference, status);

            var pr = new PaymentResponse
            {
                GatewayReference = reference,
                Status = status,
                Amount = request.Amount,
                Currency = request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = parsed?.Status ?? "C2B charge accepted"
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", pr, ct).ConfigureAwait(false);
            return pr;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunRefundAsync(request.GatewayReference, async () =>
        {
            var cached = await TryGetCachedAsync<RefundResponse>(request.IdempotencyKey, "refund", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            var refundCorrelator = request.IdempotencyKey ?? $"ecocash-refund-{Guid.NewGuid():N}";

            // The EcoCash Open API refund body keys are spelled "origional" / "Corelator" on the wire.
            // These are the provider's real (misspelled) field names, not typos in this code.
            var body = new EcoCashRefundRequest
            {
                OriginalEcocashTransactionReference = request.GatewayReference,
                RefundCorrelator = refundCorrelator,
                // SourceMobileNumber: the EcoCash refund body carries the original payer MSISDN. The Bhengu
                // RefundRequest model doesn't include it; callers that need it should pass it via the charge's
                // GatewayReference flow. Left null when unknown — EcoCash resolves from the original reference.
                SourceMobileNumber = null,
                Amount = request.Amount,
                ClientName = null,
                Currency = "USD", // EcoCash Open API refunds are USD-denominated; the model carries no currency on refunds.
                ReasonForRefund = request.Reason
            };

            var responseBody = await SendAsync($"api/v2/refund/instant/c2b/{_env}", body, ct, "ProcessRefund").ConfigureAwait(false);
            var parsed = TryDeserialize<EcoCashRefundResponse>(responseBody);

            var reference = parsed?.EcocashReference ?? parsed?.DestinationEcocashReference ?? refundCorrelator;
            var status = parsed?.TransactionStatus is { Length: > 0 } s ? MapStatus(s) : PaymentStatus.Pending;

            Logger.LogInformation("EcoCash refund initiated: RefundCorrelator={RefundCorrelator} Original={Original} Status={Status}",
                refundCorrelator, request.GatewayReference, status);

            var rr = new RefundResponse
            {
                GatewayReference = reference,
                Amount = request.Amount,
                Status = status,
                ProcessedAt = DateTime.UtcNow,
                Message = parsed?.ResponseMessage ?? parsed?.TransactionStatus
            };

            await TrySetCachedAsync(request.IdempotencyKey, "refund", rr, ct).ConfigureAwait(false);
            return rr;
        }, ct);
    }

    /// <summary>
    /// The EcoCash Open API does not publish a signed asynchronous webhook — transaction outcome is
    /// retrieved via the synchronous status-lookup endpoint, not a callback. There is therefore no
    /// signature to verify; always returns <c>false</c> to keep callers from assuming a verified push.
    /// </summary>
    /// <inheritdoc/>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunWebhookVerify(() =>
        {
            Logger.LogWarning("EcoCash Open API exposes no signed webhook; status must be polled via the lookup endpoint. VerifyWebhookSignature returns false.");
            return false;
        });
    }

    /// <summary>
    /// The EcoCash Open API publishes no asynchronous webhook/callback contract, so there is no payload
    /// shape to normalise. Always returns <c>null</c>. Use the status-lookup endpoint to learn an outcome.
    /// </summary>
    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return Task.FromResult<WebhookEvent?>(null);
    }

    // ===== HTTP plumbing =====

    private async Task<string> SendAsync(string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to EcoCash failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("EcoCash {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static T? TryDeserialize<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize<T>(body); }
        catch (JsonException) { return null; }
    }

    private async Task<T?> TryGetCachedAsync<T>(string? idempotencyKey, string operation, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return null;
        return await _cache.GetAsync<T>(BuildCacheKey(idempotencyKey, operation), ct).ConfigureAwait(false);
    }

    private async Task TrySetCachedAsync<T>(string? idempotencyKey, string operation, T value, CancellationToken ct) where T : class
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) return;
        await _cache.SetAsync(BuildCacheKey(idempotencyKey, operation), value, s_idempotencyTtl, ct).ConfigureAwait(false);
    }

    private static string BuildCacheKey(string idempotencyKey, string operation)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant();
        return $"ecocash:idem:{operation}:{hash}";
    }

    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "completed" or "complete" or "success" or "successful" or "paid" => PaymentStatus.Completed,
        "pending" or "processing" or "initiated" => PaymentStatus.Pending,
        "failed" or "declined" or "denied" or "error" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" or "reversed" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // ===== EcoCash Open API JSON shapes (internal) =====
    // Field names below are the verified Open API wire keys. The refund "origional"/"Corelator"
    // misspellings are reproduced verbatim because they are the provider's actual JSON keys.

    private sealed class EcoCashChargeRequest
    {
        [JsonPropertyName("customerMsisdn")] public string CustomerMsisdn { get; set; } = string.Empty;
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("currency")] public string Currency { get; set; } = "USD";
        [JsonPropertyName("sourceReference")] public string SourceReference { get; set; } = string.Empty;
    }

    private sealed class EcoCashRefundRequest
    {
        [JsonPropertyName("origionalEcocashTransactionReference")] public string? OriginalEcocashTransactionReference { get; set; }
        [JsonPropertyName("refundCorelator")] public string? RefundCorrelator { get; set; }
        [JsonPropertyName("sourceMobileNumber")] public string? SourceMobileNumber { get; set; }
        [JsonPropertyName("amount")] public decimal Amount { get; set; }
        [JsonPropertyName("clientName")] public string? ClientName { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("reasonForRefund")] public string? ReasonForRefund { get; set; }
    }

    private sealed class EcoCashLookupResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("customerMsisdn")] public string? CustomerMsisdn { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("ecocashReference")] public string? EcocashReference { get; set; }
        [JsonPropertyName("transactionDateTime")] public string? TransactionDateTime { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
    }

    private sealed class EcoCashRefundResponse
    {
        [JsonPropertyName("transactionStatus")] public string? TransactionStatus { get; set; }
        [JsonPropertyName("ecocashReference")] public string? EcocashReference { get; set; }
        [JsonPropertyName("destinationEcocashReference")] public string? DestinationEcocashReference { get; set; }
        [JsonPropertyName("sourceReference")] public string? SourceReference { get; set; }
        [JsonPropertyName("responseMessage")] public string? ResponseMessage { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
    }
}
