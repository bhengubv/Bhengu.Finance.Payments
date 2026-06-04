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
using Bhengu.Finance.Payments.Core.Models.Dispute;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Flutterwave.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Flutterwave.Providers;

/// <summary>
/// Flutterwave disputes / chargebacks provider. Wraps the <c>/v3/chargebacks</c>,
/// <c>/v3/chargebacks/{id}</c>, <c>/v3/chargebacks/{id}/contest</c>, and
/// <c>/v3/chargebacks/{id}/accept</c> endpoints.
/// </summary>
/// <remarks>
/// Flutterwave's chargeback endpoints are merchant-scoped: list pages on a 20-row limit by default.
/// Contesting requires the merchant to submit a free-text explanation; the file-upload step
/// uses a separate <c>/v3/chargebacks/{id}/upload-evidence</c> call which this SDK does not yet
/// proxy — pass file references via <see cref="DisputeEvidence.FileReferences"/> after uploading
/// them out-of-band through the Flutterwave dashboard.
/// </remarks>
public sealed class FlutterwaveDisputeProvider : IDisputeProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly HttpClient _httpClient;
    private readonly FlutterwaveOptions _options;
    private readonly ILogger<FlutterwaveDisputeProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Flutterwave;

    /// <summary>Create a new Flutterwave dispute provider.</summary>
    public FlutterwaveDisputeProvider(
        HttpClient httpClient,
        IOptions<FlutterwaveOptions> options,
        ILogger<FlutterwaveDisputeProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(FlutterwaveOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.flutterwave.com/");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.SecretKey);
    }

    /// <inheritdoc />
    public async Task<Dispute?> GetDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disputeReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "dispute.get");
        try
        {
            var raw = await SendAsync(HttpMethod.Get, $"v3/chargebacks/{Uri.EscapeDataString(disputeReference)}", body: null, ct, "GetDispute").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FlutterwaveDisputeEnvelope<FlutterwaveDisputeBody>>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            if (response?.Data is null)
                return null;

            return MapDispute(response.Data);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Dispute>> ListDisputesAsync(DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
    {
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "dispute.list");
        try
        {
            var query = new List<string> { "per_page=50" };
            if (fromUtc.HasValue)
                query.Add($"from={Uri.EscapeDataString(fromUtc.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");
            if (toUtc.HasValue)
                query.Add($"to={Uri.EscapeDataString(toUtc.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))}");

            var path = "v3/chargebacks?" + string.Join("&", query);
            var raw = await SendAsync(HttpMethod.Get, path, body: null, ct, "ListDisputes").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FlutterwaveDisputeEnvelope<List<FlutterwaveDisputeBody>>>(raw, DeserializeOptions);

            _logger.LogInformation("Flutterwave listed {Count} disputes between {From:o} and {To:o}",
                response?.Data?.Count ?? 0, fromUtc, toUtc);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            var list = new List<Dispute>(response?.Data?.Count ?? 0);
            if (response?.Data is null) return list;
            foreach (var d in response.Data)
                list.Add(MapDispute(d));
            return list;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Dispute> SubmitEvidenceAsync(string disputeReference, DisputeEvidence evidence, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disputeReference);
        ArgumentNullException.ThrowIfNull(evidence);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "dispute.contest");
        try
        {
            var body = new
            {
                comment = evidence.Explanation ?? "Contesting chargeback",
                response_files = evidence.FileReferences,
                shipping_proof = evidence.ShippingTrackingNumber,
                customer_name = evidence.CustomerName,
                customer_email = evidence.CustomerEmailAddress,
                billing_address = evidence.BillingAddress,
                shipping_address = evidence.ShippingAddress,
                shipping_carrier = evidence.ShippingCarrier,
                shipping_date = evidence.ShippingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };

            var raw = await SendAsync(HttpMethod.Post, $"v3/chargebacks/{Uri.EscapeDataString(disputeReference)}/contest", body, ct, "ContestDispute").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FlutterwaveDisputeEnvelope<FlutterwaveDisputeBody>>(raw, DeserializeOptions);

            _logger.LogInformation("Flutterwave dispute contested: {DisputeId}", response?.Data?.Id);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return response?.Data is null
                ? new Dispute
                {
                    Reference = disputeReference,
                    ChargeReference = string.Empty,
                    Amount = 0m,
                    Currency = "NGN",
                    Status = DisputeStatus.UnderReview,
                    OpenedAt = DateTime.UtcNow
                }
                : MapDispute(response.Data);
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<Dispute> AcceptDisputeAsync(string disputeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(disputeReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "dispute.accept");
        try
        {
            var raw = await SendAsync(HttpMethod.Post, $"v3/chargebacks/{Uri.EscapeDataString(disputeReference)}/accept", body: new { }, ct, "AcceptDispute").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<FlutterwaveDisputeEnvelope<FlutterwaveDisputeBody>>(raw, DeserializeOptions);

            _logger.LogInformation("Flutterwave dispute accepted: {DisputeId}", response?.Data?.Id);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return response?.Data is null
                ? new Dispute
                {
                    Reference = disputeReference,
                    ChargeReference = string.Empty,
                    Amount = 0m,
                    Currency = "NGN",
                    Status = DisputeStatus.Accepted,
                    OpenedAt = DateTime.UtcNow
                }
                : MapDispute(response.Data) with { Status = DisputeStatus.Accepted };
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private static Dispute MapDispute(FlutterwaveDisputeBody d)
    {
        var status = d.Status?.ToLowerInvariant() switch
        {
            "pending" or "open" => DisputeStatus.NeedsResponse,
            "under_review" or "in_progress" => DisputeStatus.UnderReview,
            "won" or "merchant_won" or "resolved_merchant" => DisputeStatus.Won,
            "lost" or "merchant_lost" or "resolved_customer" => DisputeStatus.Lost,
            "accepted" => DisputeStatus.Accepted,
            "arbitration" => DisputeStatus.Arbitration,
            "expired" => DisputeStatus.Expired,
            _ => DisputeStatus.NeedsResponse
        };

        return new Dispute
        {
            Reference = d.Id?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ChargeReference = d.TransactionId?.ToString(CultureInfo.InvariantCulture) ?? d.FlwRef ?? string.Empty,
            Amount = d.Amount ?? 0m,
            Currency = d.Currency ?? "NGN",
            Status = status,
            ReasonCode = d.Reason,
            ReasonDescription = d.ReasonDescription,
            OpenedAt = d.CreatedAt ?? DateTime.UtcNow,
            EvidenceDueBy = d.DueDate,
            ChargebackFee = d.ChargebackFee
        };
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, SerializeOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Flutterwave failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Flutterwave {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class FlutterwaveDisputeEnvelope<TData>
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public TData? Data { get; set; }
    }

    private sealed class FlutterwaveDisputeBody
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("transaction_id")] public long? TransactionId { get; set; }
        [JsonPropertyName("flw_ref")] public string? FlwRef { get; set; }
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("reason_description")] public string? ReasonDescription { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("due_date")] public DateTime? DueDate { get; set; }
        [JsonPropertyName("created_at")] public DateTime? CreatedAt { get; set; }
        [JsonPropertyName("chargeback_fee")] public decimal? ChargebackFee { get; set; }
    }
}
