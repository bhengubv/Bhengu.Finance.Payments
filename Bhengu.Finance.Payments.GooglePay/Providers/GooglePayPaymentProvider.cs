// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Validation;
using Bhengu.Finance.Payments.Google.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Google.Providers;

/// <summary>
/// Google Pay payment provider. Google Pay does not settle payments itself — it returns an
/// encrypted Payment Method Token that the merchant forwards to a real payment processor.
/// <para>
/// This provider validates the token shape, tags the request with
/// <c>payment_source=google_pay</c>, then delegates the actual charge to the
/// <see cref="GooglePayOptions.DownstreamProcessor"/> (a registered
/// <see cref="IPaymentGatewayProvider"/> such as Stripe).
/// </para>
/// </summary>
public sealed class GooglePayPaymentProvider : IPaymentGatewayProvider, IRequiresPostConstructionValidation
{
    private readonly IServiceProvider _services;
    private readonly GooglePayOptions _options;
    private readonly ILogger<GooglePayPaymentProvider> _logger;

    public string ProviderName => ProviderNames.GooglePay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Tokeniser |
        ProviderCapabilities.Cards;

    /// <summary>
    /// Called at app startup by <see cref="BhenguPaymentStartupValidator"/>. Verifies that the
    /// configured downstream processor is actually registered in DI. Fails fast at startup if
    /// missing — instead of on the first inbound Google Pay request.
    /// </summary>
    public void Validate() => ResolveDownstream();

    public GooglePayPaymentProvider(
        IServiceProvider services,
        IOptions<GooglePayOptions> options,
        ILogger<GooglePayPaymentProvider> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(GooglePayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.DownstreamProcessor))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(GooglePayOptions.DownstreamProcessor)} is required (e.g. 'stripe')");
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateGooglePayToken(request.PaymentMethodToken);

        var downstream = ResolveDownstream();

        var enrichedMetadata = MergeMetadata(request.Metadata, new Dictionary<string, string>
        {
            ["payment_source"] = "google_pay",
            ["original_provider"] = ProviderName,
            ["google_merchant_id"] = _options.MerchantId,
            ["google_environment"] = _options.UseTestEnvironment ? "TEST" : "PRODUCTION"
        });

        var downstreamRequest = request with { Metadata = enrichedMetadata };

        _logger.LogInformation("Google Pay forwarding charge to downstream={Downstream} amount={Amount} {Currency}",
            _options.DownstreamProcessor, request.Amount, request.Currency);

        return await downstream.ProcessPaymentAsync(downstreamRequest, ct).ConfigureAwait(false);
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var downstream = ResolveDownstream();
        _logger.LogInformation("Google Pay forwarding refund to downstream={Downstream} ref={Ref} amount={Amount}",
            _options.DownstreamProcessor, request.GatewayReference, request.Amount);
        return await downstream.ProcessRefundAsync(request, ct).ConfigureAwait(false);
    }

    public bool VerifyWebhookSignature(string payload, string signature) => false;

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default) =>
        Task.FromResult<WebhookEvent?>(null);

    private IPaymentGatewayProvider ResolveDownstream()
    {
        var providers = _services.GetServices<IPaymentGatewayProvider>();
        var downstream = providers.FirstOrDefault(p =>
            string.Equals(p.ProviderName, _options.DownstreamProcessor, StringComparison.OrdinalIgnoreCase)
            && p.ProviderName != ProviderName);

        if (downstream is null)
            throw new ProviderConfigurationException(ProviderName,
                $"No registered IPaymentGatewayProvider matches DownstreamProcessor='{_options.DownstreamProcessor}'. " +
                "Register the downstream processor (e.g. AddStripePayments) before AddGooglePayments.");

        return downstream;
    }

    private static void ValidateGooglePayToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new PaymentDeclinedException("googlepay", "missing_token",
                "PaymentMethodToken is required and must contain the Google Pay PaymentMethodToken JSON");

        try
        {
            using var doc = JsonDocument.Parse(token);
            var root = doc.RootElement;
            // Google Pay PaymentMethodToken minimum shape: { protocolVersion, signature, signedMessage }
            // The format varies by API version (DIRECT vs ECv1 vs ECv2). All have signature + signedMessage.
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("signature", out _) ||
                !root.TryGetProperty("signedMessage", out _))
            {
                throw new PaymentDeclinedException("googlepay", "invalid_token_shape",
                    "Token is not a valid Google Pay PaymentMethodToken (missing signature or signedMessage)");
            }
        }
        catch (JsonException ex)
        {
            throw new PaymentDeclinedException("googlepay", "invalid_token_json",
                "Google Pay PaymentMethodToken is not valid JSON", ex);
        }
    }

    private static IReadOnlyDictionary<string, string> MergeMetadata(
        IReadOnlyDictionary<string, string>? incoming,
        Dictionary<string, string> additions)
    {
        var result = new Dictionary<string, string>(additions);
        if (incoming is null) return result;
        foreach (var (k, v) in incoming)
            result.TryAdd(k, v);
        return result;
    }
}
