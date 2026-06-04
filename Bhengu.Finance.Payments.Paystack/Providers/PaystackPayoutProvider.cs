// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Bhengu.Finance.Payments.Paystack.Internals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paystack.Providers;

/// <summary>
/// Standalone Paystack <see cref="IPayoutProvider"/> implementation that adds idempotency-key
/// passthrough on top of <c>/transfer</c>. Distinct from <see cref="PaystackPaymentProvider"/>'s
/// own <see cref="IPayoutProvider"/> surface to allow consumers to use the typed payout pipeline
/// independently of the general payment surface.
/// </summary>
/// <remarks>
/// Paystack transfer destinations are <em>Transfer Recipients</em> (NUBAN-backed). Pass the
/// recipient code directly or prefix with <c>recipient-</c> for parity with the existing
/// <see cref="PaystackPaymentProvider.ProcessPayoutAsync"/> convention.
/// </remarks>
public sealed class PaystackPayoutProvider : BhenguProviderBase, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaystackOptions _options;
    private readonly PaystackIdempotencyCache _idempotency;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Paystack;

    /// <summary>Construct a payout provider. Designed to be registered via DI.</summary>
    public PaystackPayoutProvider(
        HttpClient httpClient,
        IOptions<PaystackOptions> options,
        ILogger<PaystackPayoutProvider> logger,
        PaystackIdempotencyCache idempotency)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _idempotency = idempotency ?? throw new ArgumentNullException(nameof(idempotency));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaystackOptions.SecretKey)} is required");

        PaystackHttpClient.ConfigureClient(_httpClient, _options);
    }

    /// <inheritdoc/>
    public Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunPayoutAsync(request.Currency,
            () => _idempotency.GetOrAddAsync(request.IdempotencyKey, () => ProcessPayoutCoreAsync(request, ct)),
            ct);
    }

    private async Task<PayoutResponse> ProcessPayoutCoreAsync(PayoutRequest request, CancellationToken ct)
    {
        var recipientCode = request.DestinationToken.StartsWith("recipient-", StringComparison.Ordinal)
            ? request.DestinationToken["recipient-".Length..]
            : request.DestinationToken;

        var amountInSmallestUnit = (long)(request.Amount * 100m);
        var body = new
        {
            source = "balance",
            recipient = recipientCode,
            amount = amountInSmallestUnit,
            currency = request.Currency.ToUpperInvariant(),
            reason = request.Description,
            reference = request.IdempotencyKey ?? $"transfer-{Guid.NewGuid():N}"
        };

        var responseBody = await PaystackHttpClient.SendAsync(
            _httpClient, Logger, HttpMethod.Post, "transfer", body, "ProcessPayout", ct).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<PaystackTransferResponse>(responseBody, PaystackHttpClient.Json);

        Logger.LogInformation("Paystack transfer created: {Reference} status={Status}",
            response?.Data?.Reference, response?.Data?.Status);

        return new PayoutResponse
        {
            GatewayReference = response?.Data?.Reference ?? string.Empty,
            Status = MapStatus(response?.Data?.Status),
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    private static PaymentStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" => PaymentStatus.Completed,
        "pending" or "processing" or "queued" or "ongoing" => PaymentStatus.Pending,
        "failed" or "abandoned" or "reversed" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        _ => PaymentStatus.Pending
    };

    private sealed class PaystackTransferResponse
    {
        [JsonPropertyName("status")] public bool Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("data")] public PaystackTransferData? Data { get; set; }
    }

    private sealed class PaystackTransferData
    {
        [JsonPropertyName("transfer_code")] public string? TransferCode { get; set; }
        [JsonPropertyName("reference")] public string? Reference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
