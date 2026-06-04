// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.ThreeDSecure;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Razorpay 3-D Secure provider. Wraps Razorpay's PaymentMethod 3DS flow over the S2S
/// <c>/v1/payments/create/json</c> endpoint with
/// <c>payment_method=card&amp;recurring=preferred&amp;authentication.three_ds=mandatory</c>.
/// </summary>
/// <remarks>
/// Razorpay's S2S 3DS flow returns either an ACS redirect URL (challenge required) or a
/// frictionless authorisation in the same response shape. The merchant POSTs card-token +
/// the explicit 3DS preference and Razorpay answers with <c>next.action=redirect</c> +
/// <c>next.action.url</c> pointing at the issuer ACS. Once the payer completes the challenge
/// Razorpay fires <c>payment.captured</c> / <c>payment.failed</c> webhooks against the
/// returned <c>payment_id</c>.
/// </remarks>
public sealed class RazorpayThreeDSecureProvider : IThreeDSecureProvider
{
    private readonly RazorpayHttpClient _http;
    private readonly ILogger<RazorpayThreeDSecureProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Razorpay;

    /// <summary>Create a new Razorpay 3DS provider bound to the supplied HTTP client and options.</summary>
    public RazorpayThreeDSecureProvider(
        HttpClient httpClient,
        IOptions<RazorpayOptions> options,
        ILogger<RazorpayThreeDSecureProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _http = new RazorpayHttpClient(httpClient, options.Value, ProviderName, logger);
    }

    /// <inheritdoc />
    public async Task<ThreeDSecureChallenge> StartAuthenticationAsync(PaymentRequest chargeIntent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chargeIntent);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "three_d_secure.start");
        try
        {
            var amountInPaise = (long)(chargeIntent.Amount * 100);
            var currency = chargeIntent.Currency.ToUpperInvariant();

            var body = new
            {
                amount = amountInPaise,
                currency,
                payment_method = "card",
                customer_id = chargeIntent.CustomerId,
                token = chargeIntent.PaymentMethodToken,
                recurring = "preferred",
                authentication = new
                {
                    three_ds = "mandatory",
                    challenge_indicator = "01"
                },
                description = chargeIntent.Description,
                notes = chargeIntent.Metadata
            };

            var raw = await _http.SendAsync(HttpMethod.Post, "v1/payments/create/json", body, ct, "StartAuthentication", chargeIntent.IdempotencyKey).ConfigureAwait(false);
            var response = RazorpayHttpClient.DeserialiseOrThrow<RazorpayPaymentJsonResponse>(raw, ProviderName, "StartAuthentication");

            _logger.LogInformation("Razorpay 3DS challenge: paymentId={PaymentId} status={Status} nextAction={NextAction}",
                response.Id, response.Status, response.Next?.Action);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            var status = response.Status?.ToLowerInvariant() switch
            {
                "captured" or "authorized" or "authenticated" => ThreeDSecureStatus.Authenticated,
                "created" or "pending" => ThreeDSecureStatus.ChallengeRequired,
                "attempted" => ThreeDSecureStatus.Attempted,
                "failed" or "declined" => ThreeDSecureStatus.Failed,
                _ => string.Equals(response.Next?.Action, "redirect", StringComparison.OrdinalIgnoreCase)
                    ? ThreeDSecureStatus.ChallengeRequired
                    : ThreeDSecureStatus.NotRequired
            };

            return new ThreeDSecureChallenge
            {
                Status = status,
                ChallengeReference = response.Id ?? string.Empty,
                RedirectUrl = response.Next?.Url,
                ProtocolVersion = response.Authentication?.Version ?? "2.2.0",
                DsTransactionId = response.Authentication?.DsTransactionId
            };
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<ThreeDSecureChallenge> GetChallengeAsync(string challengeReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challengeReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "three_d_secure.get");
        try
        {
            var raw = await _http.GetAsync($"v1/payments/{Uri.EscapeDataString(challengeReference)}", ct, "GetChallenge").ConfigureAwait(false);
            var response = RazorpayHttpClient.DeserialiseOrThrow<RazorpayPaymentJsonResponse>(raw, ProviderName, "GetChallenge");

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            var status = response.Status?.ToLowerInvariant() switch
            {
                "captured" or "authorized" or "authenticated" => ThreeDSecureStatus.Authenticated,
                "created" or "pending" => ThreeDSecureStatus.ChallengeRequired,
                "attempted" => ThreeDSecureStatus.Attempted,
                "failed" or "declined" => ThreeDSecureStatus.Failed,
                _ => ThreeDSecureStatus.Failed
            };

            return new ThreeDSecureChallenge
            {
                Status = status,
                ChallengeReference = challengeReference,
                ProtocolVersion = response.Authentication?.Version ?? "2.2.0",
                DsTransactionId = response.Authentication?.DsTransactionId
            };
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private sealed class RazorpayPaymentJsonResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("next")] public RazorpayPaymentNext? Next { get; set; }
        [JsonPropertyName("authentication")] public RazorpayPaymentAuth? Authentication { get; set; }
    }

    private sealed class RazorpayPaymentNext
    {
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class RazorpayPaymentAuth
    {
        [JsonPropertyName("ds_transaction_id")] public string? DsTransactionId { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
    }
}
