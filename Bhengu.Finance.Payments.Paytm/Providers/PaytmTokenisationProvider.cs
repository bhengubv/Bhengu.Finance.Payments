// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
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
public sealed class PaytmTokenisationProvider : ITokenisationProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly ConcurrentDictionary<string, PaymentMethod> TokenCache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly PaytmOptions _options;
    private readonly ILogger<PaytmTokenisationProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.Paytm;

    /// <summary>Create a new Paytm tokenisation provider.</summary>
    public PaytmTokenisationProvider(
        HttpClient httpClient,
        IOptions<PaytmOptions> options,
        ILogger<PaytmTokenisationProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

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

            var serializedBody = JsonSerializer.Serialize(bodyPayload, SerializeOptions);
            var signature = ComputeChecksum(serializedBody);

            var envelope = new { body = bodyPayload, head = new { signature } };

            var raw = await SendAsync(HttpMethod.Post, "theia/api/v2/vault/tokeniseCard", envelope, ct, "TokeniseCard").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PaytmVaultEnvelope<PaytmVaultTokenBody>>(raw, DeserializeOptions);

            _logger.LogInformation("Paytm card vaulted: token={Token} status={Status}",
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

            TokenCache[method.Token] = method;
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            return method;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return Task.FromResult(TokenCache.TryGetValue(token, out var m) ? m : null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);
        var list = TokenCache.Values.Where(m => m.CustomerId == customerId).ToList();
        return Task.FromResult<IReadOnlyList<PaymentMethod>>(list);
    }

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
                await SendAsync(HttpMethod.Post, "theia/api/v2/vault/deleteCard", envelope, ct, "DeleteCard").ConfigureAwait(false);
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

    private async Task<string> SendAsync(HttpMethod method, string path, object body, CancellationToken ct, string operation)
    {
        var json = JsonSerializer.Serialize(body, SerializeOptions);
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
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Paytm failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Paytm {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private string ComputeChecksum(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.MerchantKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(hash);
    }

    private sealed class PaytmVaultEnvelope<TBody>
    {
        [JsonPropertyName("body")] public TBody? Body { get; set; }
    }

    private sealed class PaytmVaultTokenBody
    {
        [JsonPropertyName("resultInfo")] public PaytmResultInfo? ResultInfo { get; set; }
        [JsonPropertyName("cardToken")] public string? CardToken { get; set; }
        [JsonPropertyName("cardScheme")] public string? CardScheme { get; set; }
    }

    private sealed class PaytmResultInfo
    {
        [JsonPropertyName("resultStatus")] public string? ResultStatus { get; set; }
        [JsonPropertyName("resultCode")] public string? ResultCode { get; set; }
        [JsonPropertyName("resultMsg")] public string? ResultMsg { get; set; }
    }
}
