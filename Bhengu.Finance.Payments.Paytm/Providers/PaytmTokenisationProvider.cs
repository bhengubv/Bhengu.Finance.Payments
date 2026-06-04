// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Paytm.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Paytm.Providers;

/// <summary>
/// Paytm vault provider. Wraps Paytm Wallet identity (linkPaytmWallet) and Paytm Card Vault
/// (tokeniseCard / linkCardWithWallet) endpoints under <c>theia/api/v2/vault</c>.
/// </summary>
/// <remarks>
/// Paytm's vault model couples two concepts: a Paytm Wallet identity (mobile-tied) and the
/// Paytm Card Vault that maps PAN -> short-lived <c>card_token</c>. This provider exposes
/// the Card Vault flow as <see cref="TokeniseAsync"/> — the resulting <c>cardToken</c> can be
/// charged via Paytm All-in-One under <c>theia/api/v1/processTransaction?paymentMode=CARD&amp;cardToken=…</c>.
/// Paytm's vault list endpoint is per-customer; we cache the customer↔tokens mapping locally so
/// <see cref="ListPaymentMethodsAsync"/> stays consistent across deletions.
/// </remarks>
public sealed class PaytmTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    internal static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    internal static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    internal static readonly ConcurrentDictionary<string, PaymentMethod> TokenCache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly PaytmOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Paytm;

    /// <summary>Create a new Paytm tokenisation provider.</summary>
    public PaytmTokenisationProvider(
        HttpClient httpClient,
        IOptions<PaytmOptions> options,
        ILogger<PaytmTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://securegw-stage.paytm.in/")
                : (_options.BaseUrl ?? "https://securegw.paytm.in/"));
        }
    }

    /// <inheritdoc />
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Task.FromResult(TokenCache.TryGetValue(token, out var m) ? m : null);
    }

    /// <inheritdoc />
#pragma warning disable CS1998 // intentionally async with no awaits — Paytm uses an in-memory cache for vault listing.
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        foreach (var m in TokenCache.Values)
        {
            if (m.CustomerId != customerId) continue;
            ct.ThrowIfCancellationRequested();
            yield return m;
        }
    }
#pragma warning restore CS1998

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.delete");
        try
        {
            var bodyPayload = new { mid = _options.MerchantId, cardToken = token };
            var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
            var signature = ComputeChecksum(serializedBody);
            var envelope = new { body = bodyPayload, head = new { signature } };

            try
            {
                await PaytmHttp.SendAsync(_httpClient, Logger, ProviderName, HttpMethod.Post, "theia/api/v2/vault/deleteCard", envelope, ct, "DeleteCard").ConfigureAwait(false);
            }
            catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
            {
                return false;
            }

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return TokenCache.TryRemove(token, out _);
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    internal string ComputeChecksum(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MerchantKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    internal sealed class PaytmVaultEnvelope<TBody>
    {
        [JsonPropertyName("body")] public TBody? Body { get; set; }
    }

    internal sealed class PaytmVaultTokenBody
    {
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
        [JsonPropertyName("cardToken")] public string? CardToken { get; set; }
        [JsonPropertyName("cardScheme")] public string? CardScheme { get; set; }
    }

    internal sealed class PaytmResultInfo
    {
        [JsonPropertyName("resultStatus")] public string? ResultStatus { get; set; }
        [JsonPropertyName("resultCode")] public string? ResultCode { get; set; }
        [JsonPropertyName("resultMsg")] public string? ResultMsg { get; set; }
    }
}

/// <summary>Shared HTTP helper for Paytm vault providers.</summary>
internal static class PaytmHttp
{
    public static async Task<string> SendAsync(HttpClient httpClient, ILogger logger, string providerName, HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, PaytmTokenisationProvider.SerializeOptions);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(providerName, "HTTP request to Paytm failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(providerName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Paytm {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(providerName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(providerName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}

/// <summary>
/// PCI-DSS SAQ-D-scope Paytm raw-card tokenisation. Sends raw PAN to Paytm's
/// <c>theia/api/v2/vault/tokeniseCard</c> endpoint and returns the resulting card token.
/// </summary>
public sealed class PaytmRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly PaytmOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Paytm;

    /// <summary>Create a new Paytm raw-card tokenisation provider.</summary>
    public PaytmRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<PaytmOptions> options,
        ILogger<PaytmRawCardTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantId)} is required");
        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PaytmOptions.MerchantKey)} is required");

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(_options.UseSandbox
                ? (_options.SandboxUrl ?? "https://securegw-stage.paytm.in/")
                : (_options.BaseUrl ?? "https://securegw.paytm.in/"));
        }
    }

    /// <inheritdoc />
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise");
        try
        {
            var custId = request.CustomerId ?? $"cust-{Guid.NewGuid():N}";
            var bodyPayload = new
            {
                mid = _options.MerchantId,
                custId,
                cardNumber = request.Card.CardNumber,
                cardHolderName = request.Card.CardholderName,
                expiryMonth = request.Card.ExpiryMonth.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                expiryYear = request.Card.ExpiryYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cvv = request.Card.Cvv
            };

            var serializedBody = JsonSerializer.Serialize(bodyPayload, PaytmTokenisationProvider.SerializeOptions);
            var signature = ComputeChecksum(serializedBody);

            var envelope = new { body = bodyPayload, head = new { signature } };

            var raw = await PaytmHttp.SendAsync(_httpClient, Logger, ProviderName, HttpMethod.Post, "theia/api/v2/vault/tokeniseCard", envelope, ct, "TokeniseCard").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaytmTokenisationProvider.PaytmVaultEnvelope<PaytmTokenisationProvider.PaytmVaultTokenBody>>(raw, PaytmTokenisationProvider.DeserializeOptions);

            Logger.LogInformation("Paytm card vaulted: token={Token} status={Status}",
                response?.Body?.CardToken, response?.Body?.ResultInfo?.ResultStatus);

            if (response?.Body?.ResultInfo?.ResultStatus is "F" or "FAILURE")
                throw new PaymentDeclinedException(ProviderName, response.Body.ResultInfo.ResultCode ?? "vault_failed", response.Body.ResultInfo.ResultMsg ?? "Paytm tokenisation failed");

            var last4 = request.Card.CardNumber is { Length: >= 4 } pan ? pan[^4..] : null;
            var method = new PaymentMethod
            {
                Token = response?.Body?.CardToken ?? $"tkn-{Guid.NewGuid():N}",
                CustomerId = custId,
                Kind = PaymentMethodKind.Card,
                Brand = response?.Body?.CardScheme,
                Last4 = last4,
                ExpiryMonth = request.Card.ExpiryMonth,
                ExpiryYear = request.Card.ExpiryYear,
                DisplayName = request.DisplayName,
                IsDefault = request.SetAsDefault,
                CreatedAt = DateTime.UtcNow
            };

            PaytmTokenisationProvider.TokenCache[method.Token] = method;
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return method;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private string ComputeChecksum(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MerchantKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }
}
