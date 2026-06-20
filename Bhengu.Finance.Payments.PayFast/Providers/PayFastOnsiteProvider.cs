// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.PayFast.Builders;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayFast.Providers;

/// <summary>
/// PayFast Onsite (in-page popup) checkout. Server-side, this generates a payment <c>uuid</c> by POSTing a
/// signed payment request to PayFast's website host. The caller then opens the popup client-side with
/// <c>window.payfast_do_onsite_payment({ "uuid": "&lt;uuid&gt;" })</c> after loading
/// <c>https://www.payfast.co.za/onsite/engine.js</c>. Mirrors PayFast's official <c>OnsiteIntegration</c>.
/// </summary>
/// <remarks>
/// Signing is the <em>redirect</em> algorithm (fixed field order, rands), not the REST signer — handled by
/// <see cref="PayFastFormBuilder.BuildOnsitePaymentBody"/>. Onsite is NOT available in sandbox: PayFast
/// rejects it, so this provider throws early in sandbox mode.
/// </remarks>
public sealed class PayFastOnsiteProvider : BhenguProviderBase
{
    private readonly HttpClient _httpClient;
    private readonly PayFastOptions _options;
    private readonly PayFastFormBuilder _formBuilder;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.PayFast;

    /// <summary>Construct the provider. Designed to be registered via DI.</summary>
    public PayFastOnsiteProvider(
        HttpClient httpClient,
        IOptions<PayFastOptions> options,
        PayFastFormBuilder formBuilder,
        ILogger<PayFastOnsiteProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _formBuilder = formBuilder ?? throw new ArgumentNullException(nameof(formBuilder));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayFastOptions.MerchantId)} is required");
    }

    /// <summary>
    /// Generate an Onsite payment identifier (UUID) to hand to the client-side popup. Amount is in rands.
    /// Throws in sandbox (PayFast does not offer Onsite there).
    /// </summary>
    public Task<string> GeneratePaymentIdentifierAsync(
        string mPaymentId,
        decimal amount,
        string itemName,
        string? emailAddress = null,
        string? returnUrl = null,
        string? cancelUrl = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(mPaymentId);
        ArgumentException.ThrowIfNullOrEmpty(itemName);
        return RunOperationAsync("onsite_identifier",
            () => GenerateCoreAsync(mPaymentId, amount, itemName, emailAddress, returnUrl, cancelUrl, ct), ct);
    }

    private async Task<string> GenerateCoreAsync(
        string mPaymentId, decimal amount, string itemName, string? email, string? returnUrl, string? cancelUrl, CancellationToken ct)
    {
        if (_options.UseSandbox)
            throw new BhenguPaymentException(ProviderName,
                "PayFast Onsite checkout is not available in sandbox mode.", "onsite_sandbox_unsupported");

        var body = _formBuilder.BuildOnsitePaymentBody(
            mPaymentId, amount, itemName, emailAddress: email, returnUrl: returnUrl, cancelUrl: cancelUrl);

        using var content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
        using var req = new HttpRequestMessage(HttpMethod.Post, _formBuilder.OnsiteProcessUrl) { Content = content };

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("PayFast Onsite identifier failed: {Status} {Body}", response.StatusCode, responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("uuid", out var uuid) && uuid.GetString() is { Length: > 0 } id)
        {
            Logger.LogInformation("PayFast Onsite identifier created: m_payment_id={MPaymentId}", mPaymentId);
            return id;
        }

        throw new BhenguPaymentException(ProviderName,
            $"PayFast Onsite did not return a uuid. Response: {responseBody}", "onsite_no_uuid");
    }
}
