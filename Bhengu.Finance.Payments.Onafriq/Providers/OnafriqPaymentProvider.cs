// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Core.Caching;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Webhooks;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Security;
using Bhengu.Finance.Payments.Onafriq.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Onafriq.Providers;

/// <summary>
/// Onafriq (formerly MFS Africa) cross-border mobile-money provider.
/// <para>
/// Onafriq's "Portal API" is the Beyonic API (Onafriq acquired Beyonic in 2020 and rebranded the
/// developer portal). Wire details below are taken from the public docs:
/// <list type="bullet">
///   <item>Base URL + Token auth: https://developer.mfsafrica.com/docs/api-endpoints and https://developer.mfsafrica.com/docs/api-key</item>
///   <item>Payout (Payment): https://github.com/beyonic/api-docs/blob/master/source/includes/sending_funds/_payments.md → <c>POST /api/payments</c></item>
///   <item>Payin (Collection Request): https://github.com/beyonic/api-docs/blob/master/source/includes/collecting_funds/_collection_requests.md → <c>POST /api/collectionrequests</c></item>
///   <item>Webhooks: https://github.com/beyonic/api-docs/blob/master/source/includes/methods/_webhooks.md (optional HTTP Basic Auth — NOT HMAC)</item>
/// </list>
/// Onafriq is primarily a disbursement / collection rail across 35+ African countries.
/// <see cref="ProcessPaymentAsync"/> maps to a Collection Request (request-to-pay / payin) and
/// <see cref="ProcessPayoutAsync"/> maps to a Payment (disbursement). Refunds are not supported by the
/// API: money movement is one-directional and a reversal must be issued as a new opposite transaction.
/// </para>
/// </summary>
[ProviderVerificationStatus(ProviderVerificationStatus.DocsOnly, Notes = "Wire format built from public Onafriq/MFS Africa (Beyonic) documentation; never sandbox-verified.")]
public sealed class OnafriqPaymentProvider : BhenguProviderBase, IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly OnafriqOptions _options;
    private readonly IBhenguDistributedCache _cache;
    private static readonly TimeSpan s_idempotencyTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Onafriq;

    /// <inheritdoc/>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney |
        ProviderCapabilities.CrossBorder |
        ProviderCapabilities.Idempotency |
        ProviderCapabilities.TypedWebhooks;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public OnafriqPaymentProvider(
        HttpClient httpClient,
        IOptions<OnafriqOptions> options,
        ILogger<OnafriqPaymentProvider> logger,
        IBhenguDistributedCache? cache = null)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? new InMemoryBhenguDistributedCache();

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(OnafriqOptions.ApiKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            // Production cross-border host. There is no separate sandbox host — testing uses the same
            // base URL with the BXC test currency. Source: https://developer.mfsafrica.com/docs/api-endpoints
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mfsafrica.com/api/");
        }

        // Token Based Authentication: "Authorization: Token <api_key>".
        // Source: https://developer.mfsafrica.com/docs/api-key and beyonic/api-docs _authentication.md.
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", _options.ApiKey);

        if (!string.IsNullOrWhiteSpace(_options.ApiVersion))
        {
            // Optional version pin. Source: beyonic/api-docs _versioning.md ("Beyonic-Version" header).
            _httpClient.DefaultRequestHeaders.Remove("Beyonic-Version");
            _httpClient.DefaultRequestHeaders.Add("Beyonic-Version", _options.ApiVersion);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Maps to a Beyonic/Onafriq <b>Collection Request</b> (request-to-pay / payin): the payer is
    /// prompted on their phone to authorise the debit. <c>PaymentMethodToken</c> carries the payer's
    /// MSISDN in international format (e.g. "+254700000000"). Source:
    /// https://github.com/beyonic/api-docs/blob/master/source/includes/collecting_funds/_collection_requests.md
    /// </remarks>
    public Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunChargeAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PaymentResponse>(request.IdempotencyKey, "charge", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            // collectionrequests fields: phonenumber, amount, currency, account?, reason?, metadata?.
            var form = new List<KeyValuePair<string, string>>
            {
                new("phonenumber", request.PaymentMethodToken),
                new("amount", request.Amount.ToString(CultureInfo.InvariantCulture)),
                new("currency", request.Currency.ToUpperInvariant()),
                new("reason", request.Description),
            };
            if (!string.IsNullOrWhiteSpace(_options.AccountId))
                form.Add(new("account", _options.AccountId));
            if (!string.IsNullOrWhiteSpace(_options.CallbackUrl))
                form.Add(new("callback_url", _options.CallbackUrl!));
            AppendMetadata(form, request.Metadata, request.IdempotencyKey);

            var body = await SendFormAsync("collectionrequests", form, ct, "ProcessPayment", request.IdempotencyKey).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<OnafriqCollectionResponse>(body, s_json);

            Logger.LogInformation("Onafriq collection request created: {Id} status={Status}",
                response?.Id, response?.Status);

            var pr = new PaymentResponse
            {
                GatewayReference = response?.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Status = MapStatus(response?.Status ?? "pending"),
                Amount = response?.Amount ?? request.Amount,
                Currency = response?.Currency ?? request.Currency,
                ProcessedAt = DateTime.UtcNow,
                Message = response?.ErrorMessage ?? response?.Status
            };

            await TrySetCachedAsync(request.IdempotencyKey, "charge", pr, ct).ConfigureAwait(false);
            return pr;
        }, ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Maps to a Beyonic/Onafriq <b>Payment</b> (disbursement): funds are sent to the recipient's
    /// mobile wallet. <c>DestinationToken</c> carries the recipient's MSISDN in international format
    /// (e.g. "+233244000000"). Source:
    /// https://github.com/beyonic/api-docs/blob/master/source/includes/sending_funds/_payments.md
    /// </remarks>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency, async () =>
        {
            var cached = await TryGetCachedAsync<PayoutResponse>(request.IdempotencyKey, "payout", ct).ConfigureAwait(false);
            if (cached is not null) return cached;

            // payments fields: phonenumber, amount, currency, payment_type, description?, account?, callback_url?.
            var form = new List<KeyValuePair<string, string>>
            {
                new("phonenumber", request.DestinationToken),
                new("amount", request.Amount.ToString(CultureInfo.InvariantCulture)),
                new("currency", request.Currency.ToUpperInvariant()),
                new("payment_type", "money"),
                new("description", request.Description),
            };
            if (!string.IsNullOrWhiteSpace(_options.AccountId))
                form.Add(new("account", _options.AccountId));
            if (!string.IsNullOrWhiteSpace(_options.CallbackUrl))
                form.Add(new("callback_url", _options.CallbackUrl!));
            AppendMetadata(form, request.Metadata, request.IdempotencyKey);

            var body = await SendFormAsync("payments", form, ct, "ProcessPayout", request.IdempotencyKey).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<OnafriqPaymentResponse>(body, s_json);

            Logger.LogInformation("Onafriq payment (disbursement) created: {Id} state={State}",
                response?.Id, response?.State);

            var pr = new PayoutResponse
            {
                GatewayReference = response?.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Status = MapStatus(response?.State ?? "new"),
                Amount = response?.Amount ?? request.Amount,
                Currency = response?.Currency ?? request.Currency,
                ProcessedAt = DateTime.UtcNow
            };

            await TrySetCachedAsync(request.IdempotencyKey, "payout", pr, ct).ConfigureAwait(false);
            return pr;
        }, ct);
    }

    /// <inheritdoc/>
    public Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        // The Onafriq/Beyonic API exposes no refund endpoint. Money movement is one-directional;
        // reversals must be issued as a new opposite Payment (disbursement) back to the original payer.
        // Surface this explicitly so callers do not silently lose money.
        throw new BhenguPaymentException(
            ProviderName,
            "Onafriq does not support refunds; reversals require a new opposite transaction. " +
            "Issue a payout from your merchant account back to the original payer's wallet instead.",
            providerErrorCode: "refund_unsupported");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Onafriq/Beyonic does NOT HMAC-sign webhooks. It optionally authenticates the inbound callback
    /// with HTTP Basic Auth credentials arranged with their support team. We therefore treat the
    /// inbound <c>Authorization: Basic …</c> header value as the "signature": pass it verbatim and we
    /// validate it (constant-time) against the configured username/password. Source:
    /// https://github.com/beyonic/api-docs/blob/master/source/includes/methods/_webhooks.md
    /// </remarks>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        return RunWebhookVerify(() =>
        {
            if (string.IsNullOrWhiteSpace(_options.WebhookBasicAuthUsername) ||
                string.IsNullOrWhiteSpace(_options.WebhookBasicAuthPassword))
            {
                Logger.LogWarning(
                    "Onafriq webhook Basic Auth credentials not configured — callback authentication cannot succeed. " +
                    "Set OnafriqOptions.WebhookBasicAuthUsername/Password (Onafriq does not HMAC-sign webhooks).");
                return false;
            }

            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{_options.WebhookBasicAuthUsername}:{_options.WebhookBasicAuthPassword}"));

            // Accept either the full "Basic <b64>" header or the bare base64 credential.
            var presented = signature.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)
                ? signature
                : "Basic " + signature;

            return SignatureHelpers.ConstantTimeEquals(presented, expected);
        });
    }

    /// <inheritdoc/>
    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        return RunOperationAsync("parse_webhook", () =>
        {
            try
            {
                var envelope = JsonSerializer.Deserialize<OnafriqWebhookEnvelope>(payload, s_json);
                if (envelope is null) return Task.FromResult<WebhookEvent?>(null);

                Logger.LogInformation("Parsed Onafriq webhook event: {EventType}", envelope.Hook?.Event);
                var typed = MapWebhookEvent(envelope);
                return Task.FromResult(typed);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to parse Onafriq webhook event");
                return Task.FromResult<WebhookEvent?>(null);
            }
        }, ct);
    }

    // Beyonic/Onafriq webhook envelope is { "hook": { "event": "...", ... }, "data": { ...object... } }.
    // Event names: payment.status.changed, collectionrequest.status.changed, collection.received,
    // contact.created. Source: beyonic/api-docs methods/_webhooks.md and methods/_events.md.
    private static WebhookEvent? MapWebhookEvent(OnafriqWebhookEnvelope envelope)
    {
        var data = envelope.Data;
        var reference = data?.Id?.ToString(CultureInfo.InvariantCulture);
        if (string.IsNullOrEmpty(reference)) return null;

        var amount = data?.Amount ?? 0m;
        var currency = data?.Currency ?? "USD";
        var eventName = envelope.Hook?.Event?.ToLowerInvariant();

        switch (eventName)
        {
            case "payment.status.changed":
            {
                // Disbursement lifecycle. "state": new/processed/processed_with_errors/rejected/cancelled.
                var status = MapStatus(data?.State ?? data?.Status ?? "new");
                return status switch
                {
                    PaymentStatus.Completed => new PayoutCompletedEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = envelope.Hook?.Event,
                        Category = WebhookEventCategory.PayoutCompleted,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency,
                        DestinationToken = data?.PhoneNumber
                    },
                    PaymentStatus.Failed or PaymentStatus.Cancelled => new PayoutFailedEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = envelope.Hook?.Event,
                        Category = WebhookEventCategory.PayoutFailed,
                        PayoutReference = reference,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = data?.State ?? data?.Status,
                        FailureMessage = data?.LastError
                    },
                    _ => new WebhookEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = envelope.Hook?.Event,
                        Category = WebhookEventCategory.Unknown
                    }
                };
            }

            case "collectionrequest.status.changed":
            case "collection.received":
            {
                // Collection (payin) lifecycle. "status": pending/processing_started/successful/failed/expired/reversed.
                var status = MapStatus(data?.Status ?? data?.State ?? "pending");
                return status switch
                {
                    PaymentStatus.Completed => new ChargeSucceededEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Completed,
                        EventType = envelope.Hook?.Event,
                        Category = WebhookEventCategory.ChargeSucceeded,
                        Amount = amount,
                        Currency = currency,
                        CustomerId = data?.PhoneNumber,
                        PaymentMethodToken = data?.PhoneNumber
                    },
                    PaymentStatus.Failed or PaymentStatus.Cancelled => new ChargeFailedEvent
                    {
                        GatewayReference = reference,
                        Status = status,
                        EventType = envelope.Hook?.Event,
                        Category = WebhookEventCategory.ChargeFailed,
                        Amount = amount,
                        Currency = currency,
                        FailureCode = data?.Status ?? data?.State,
                        FailureMessage = data?.ErrorMessage
                    },
                    PaymentStatus.Pending => new ChargePendingEvent
                    {
                        GatewayReference = reference,
                        Status = PaymentStatus.Pending,
                        EventType = envelope.Hook?.Event,
                        Category = WebhookEventCategory.ChargePending,
                        Amount = amount,
                        Currency = currency
                    },
                    _ => null
                };
            }

            default:
                return null;
        }
    }

    private void AppendMetadata(List<KeyValuePair<string, string>> form, IReadOnlyDictionary<string, string>? metadata, string? idempotencyKey)
    {
        // Beyonic accepts custom attributes as "metadata.<key>=<value>" form fields (up to 10).
        // Source: beyonic/api-docs _metadata.md.
        if (metadata is not null)
        {
            foreach (var kvp in metadata)
                form.Add(new($"metadata.{kvp.Key}", kvp.Value));
        }

        // Beyonic supports request de-duplication via a metadata key it indexes for duplicate detection.
        // Source: beyonic/api-docs _duplicate_requests.md. Stamp the caller's idempotency key so retried
        // POSTs are recognised upstream in addition to our local cache short-circuit.
        if (!string.IsNullOrWhiteSpace(idempotencyKey) && (metadata is null || !metadata.ContainsKey("bhengu_idempotency_key")))
            form.Add(new("metadata.bhengu_idempotency_key", idempotencyKey));
    }

    private async Task<string> SendFormAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct, string operation, string? idempotencyKey)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form)
        };

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Onafriq {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
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
        return $"onafriq:idem:{operation}:{hash}";
    }

    // Maps both the disbursement "state" values (new/processed/processed_with_errors/rejected/cancelled)
    // and the collection "status" values (pending/processing_started/successful/failed/expired/reversed).
    // Sources: beyonic/api-docs sending_funds/_payments.md and collecting_funds/_collection_requests.md.
    private static PaymentStatus MapStatus(string raw) => raw?.ToLowerInvariant() switch
    {
        "processed" or "successful" or "success" or "completed" => PaymentStatus.Completed,
        "new" or "pending" or "pending_dispatched" or "processing_started" or "processing" => PaymentStatus.Pending,
        "processed_with_errors" or "failed" or "rejected" or "expired" => PaymentStatus.Failed,
        "reversed" => PaymentStatus.Refunded,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    // === Onafriq/Beyonic API response shapes (internal) ===

    // POST /api/payments response. Source: beyonic/api-docs sending_funds/_payments.md.
    private sealed class OnafriqPaymentResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("last_error")] public string? LastError { get; set; }
        [JsonPropertyName("remote_transaction_id")] public string? RemoteTransactionId { get; set; }
    }

    // POST /api/collectionrequests response. Source: beyonic/api-docs collecting_funds/_collection_requests.md.
    private sealed class OnafriqCollectionResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("phonenumber")] public string? PhoneNumber { get; set; }
        [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; }
    }

    // Webhook envelope: { "hook": {...}, "data": {...} }. Source: beyonic/api-docs methods/_webhooks.md.
    private sealed class OnafriqWebhookEnvelope
    {
        [JsonPropertyName("hook")] public OnafriqWebhookHook? Hook { get; set; }
        [JsonPropertyName("data")] public OnafriqWebhookData? Data { get; set; }
    }

    private sealed class OnafriqWebhookHook
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("event")] public string? Event { get; set; }
        [JsonPropertyName("target")] public string? Target { get; set; }
    }

    private sealed class OnafriqWebhookData
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }       // payments
        [JsonPropertyName("status")] public string? Status { get; set; }     // collectionrequests
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("phonenumber")] public string? PhoneNumber { get; set; }
        [JsonPropertyName("last_error")] public string? LastError { get; set; }       // payments
        [JsonPropertyName("error_message")] public string? ErrorMessage { get; set; } // collectionrequests
    }
}
