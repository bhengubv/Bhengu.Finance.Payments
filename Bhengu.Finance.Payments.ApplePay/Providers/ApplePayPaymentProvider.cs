// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text.Json;
using Bhengu.Finance.Payments.ApplePay.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.ApplePay.Providers;

/// <summary>
/// Apple Pay payment provider. Apple Pay does not settle payments itself — it produces an
/// encrypted PKPaymentToken that the merchant forwards to a real payment processor.
/// <para>
/// This provider validates the PKPaymentToken shape, tags the request with
/// <c>payment_source=apple_pay</c>, then delegates the actual charge to the
/// <see cref="ApplePayOptions.DownstreamProcessor"/> (a registered
/// <see cref="IPaymentGatewayProvider"/> such as Stripe).
/// </para>
/// <para>
/// Refunds delegate to the same downstream processor by gateway reference. Apple Pay itself
/// has no webhook channel; the downstream processor handles webhook events.
/// </para>
/// </summary>
public sealed class ApplePayPaymentProvider : IPaymentGatewayProvider, IRequiresPostConstructionValidation
{
    private readonly IServiceProvider _services;
    private readonly ApplePayOptions _options;
    private readonly ILogger<ApplePayPaymentProvider> _logger;

    public string ProviderName => ProviderNames.ApplePay;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Tokeniser |
        ProviderCapabilities.Cards;

    /// <summary>
    /// Called at app startup by <see cref="BhenguPaymentStartupValidator"/>. Verifies that the
    /// configured downstream processor is actually registered in DI. Fails fast at startup if
    /// missing — instead of on the first inbound Apple Pay request.
    /// </summary>
    public void Validate() => ResolveDownstream();

    public ApplePayPaymentProvider(
        IServiceProvider services,
        IOptions<ApplePayOptions> options,
        ILogger<ApplePayPaymentProvider> logger)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ApplePayOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.DownstreamProcessor))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(ApplePayOptions.DownstreamProcessor)} is required (e.g. 'stripe')");
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatePkPaymentToken(request.PaymentMethodToken);

        var downstream = ResolveDownstream();

        var enrichedMetadata = MergeMetadata(request.Metadata, new Dictionary<string, string>
        {
            ["payment_source"] = "apple_pay",
            ["original_provider"] = ProviderName,
            ["apple_merchant_id"] = _options.MerchantId
        });

        var downstreamRequest = request with { Metadata = enrichedMetadata };

        _logger.LogInformation("Apple Pay forwarding charge to downstream={Downstream} amount={Amount} {Currency}",
            _options.DownstreamProcessor, request.Amount, request.Currency);

        return await downstream.ProcessPaymentAsync(downstreamRequest, ct).ConfigureAwait(false);
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var downstream = ResolveDownstream();
        _logger.LogInformation("Apple Pay forwarding refund to downstream={Downstream} ref={Ref} amount={Amount}",
            _options.DownstreamProcessor, request.GatewayReference, request.Amount);
        return await downstream.ProcessRefundAsync(request, ct).ConfigureAwait(false);
    }

    // Apple Pay has no webhook channel of its own — the downstream processor handles event delivery.
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
                "Register the downstream processor (e.g. AddStripePayments) before AddApplePayPayments.");

        return downstream;
    }

    private static void ValidatePkPaymentToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new PaymentDeclinedException("applepay", "missing_token",
                "PaymentMethodToken is required and must contain the Apple Pay PKPaymentToken JSON");

        try
        {
            using var doc = JsonDocument.Parse(token);
            var root = doc.RootElement;
            // Apple Pay PKPaymentToken minimum shape: { paymentData, paymentMethod, transactionIdentifier }
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("paymentData", out _) ||
                !root.TryGetProperty("paymentMethod", out _))
            {
                throw new PaymentDeclinedException("applepay", "invalid_token_shape",
                    "Token is not a valid Apple Pay PKPaymentToken (missing paymentData or paymentMethod)");
            }
        }
        catch (JsonException ex)
        {
            throw new PaymentDeclinedException("applepay", "invalid_token_json",
                "Apple Pay PaymentMethodToken is not valid JSON", ex);
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
