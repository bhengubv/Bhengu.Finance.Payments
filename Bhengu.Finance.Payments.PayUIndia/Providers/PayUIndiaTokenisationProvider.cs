// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

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
using Bhengu.Finance.Payments.PayUIndia.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayUIndia.Providers;

/// <summary>
/// PayU India vault provider. Wraps PayU India's stored-card token API
/// (<c>save_user_card</c>, <c>get_user_cards</c>, <c>delete_user_card</c>) on the info-service
/// (<c>info.payu.in</c>) endpoint.
/// </summary>
/// <remarks>
/// PayU India tokens are bound to the merchant's <c>user_credentials</c> (typically email or
/// customer-id). The SDK accepts the <see cref="TokeniseRequest.CustomerId"/> as
/// <c>user_credentials</c>; if null we synthesise from the cardholder name. Tokens are returned
/// as PayU India <c>card_token</c> strings (e.g. <c>0d09a3a8a8a8...</c>).
/// </remarks>
public sealed class PayUIndiaTokenisationProvider : BhenguProviderBase, ITokenisationProvider
{
    internal static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _httpClient;
    private readonly PayUIndiaOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.PayUIndia;

    /// <summary>Create a new PayU India tokenisation provider bound to the supplied HTTP client and options.</summary>
    public PayUIndiaTokenisationProvider(
        HttpClient httpClient,
        IOptions<PayUIndiaOptions> options,
        ILogger<PayUIndiaTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Salt))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.Salt)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.InfoBaseUrl ?? "https://info.payu.in/");
    }

    /// <inheritdoc />
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.get");
        try
        {
            const string command = "get_user_cards";
            var hashInput = string.Join("|", _options.MerchantKey, command, token, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = token,
                ["hash"] = hash
            };

            var raw = await PayUIndiaHttp.PostFormAsync(_httpClient, Logger, ProviderName, "merchant/postservice.php?form=2", form, ct, "GetCard").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaCardResponse>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            if (response is null || string.IsNullOrEmpty(response.CardToken))
                return null;

            return new PaymentMethod
            {
                Token = response.CardToken,
                CustomerId = response.UserCredentials,
                Kind = PaymentMethodKind.Card,
                Brand = response.CardMode,
                Last4 = response.CardNumberMasked is { Length: >= 4 } pan ? pan[^4..] : null,
                ExpiryMonth = response.ExpiryMonth,
                ExpiryYear = response.ExpiryYear,
                CreatedAt = DateTime.UtcNow
            };
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
    public async IAsyncEnumerable<PaymentMethod> ListPaymentMethodsAsync(string customerId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        List<PayUIndiaCardResponse>? items;
        using (var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.list"))
        {
            try
            {
                const string command = "get_user_cards";
                var hashInput = string.Join("|", _options.MerchantKey, command, customerId, _options.Salt);
                var hash = Sha512Hex(hashInput);

                var form = new Dictionary<string, string>
                {
                    ["key"] = _options.MerchantKey,
                    ["command"] = command,
                    ["var1"] = customerId,
                    ["hash"] = hash
                };

                var raw = await PayUIndiaHttp.PostFormAsync(_httpClient, Logger, ProviderName, "merchant/postservice.php?form=2", form, ct, "ListCards").ConfigureAwait(false);
                var response = JsonSerializer.Deserialize<PayUIndiaCardListResponse>(raw, DeserializeOptions);
                items = response?.UserCards;
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
            }
            catch
            {
                activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
                throw;
            }
        }

        if (items is null) yield break;
        foreach (var card in items)
        {
            if (string.IsNullOrEmpty(card.CardToken)) continue;
            ct.ThrowIfCancellationRequested();
            yield return new PaymentMethod
            {
                Token = card.CardToken,
                CustomerId = customerId,
                Kind = PaymentMethodKind.Card,
                Brand = card.CardMode,
                Last4 = card.CardNumberMasked is { Length: >= 4 } pan ? pan[^4..] : null,
                ExpiryMonth = card.ExpiryMonth,
                ExpiryYear = card.ExpiryYear,
                CreatedAt = DateTime.UtcNow
            };
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise.delete");
        try
        {
            const string command = "delete_user_card";
            var hashInput = string.Join("|", _options.MerchantKey, command, token, _options.Salt);
            var hash = Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = token,
                ["hash"] = hash
            };

            var raw = await PayUIndiaHttp.PostFormAsync(_httpClient, Logger, ProviderName, "merchant/postservice.php?form=2", form, ct, "DeleteCard").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaCardResponse>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return string.Equals(response?.Status, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(response?.Status, "success", StringComparison.OrdinalIgnoreCase);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return false;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    internal static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal sealed class PayUIndiaCardResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("card_token")] public string? CardToken { get; set; }
        [JsonPropertyName("user_credentials")] public string? UserCredentials { get; set; }
        [JsonPropertyName("card_mode")] public string? CardMode { get; set; }
        [JsonPropertyName("card_no")] public string? CardNumberMasked { get; set; }
        [JsonPropertyName("expiry_month")] public int? ExpiryMonth { get; set; }
        [JsonPropertyName("expiry_year")] public int? ExpiryYear { get; set; }
    }

    internal sealed class PayUIndiaCardListResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("user_cards")] public List<PayUIndiaCardResponse>? UserCards { get; set; }
    }
}

/// <summary>Shared HTTP helper for PayU India vault providers.</summary>
internal static class PayUIndiaHttp
{
    public static async Task<string> PostFormAsync(HttpClient httpClient, ILogger logger, string providerName, string path, IDictionary<string, string> form, CancellationToken ct, string operation)
    {
        using var content = new FormUrlEncodedContent(form);
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(providerName, "HTTP request to PayU India failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(providerName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("PayU India {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(providerName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(providerName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}

/// <summary>
/// PCI-DSS SAQ-D-scope PayU India raw-card tokenisation. Sends raw PAN to the info-service
/// <c>save_user_card</c> command and returns the resulting card token. Prefer the client-side
/// PayU Money / PayU India hosted checkout where possible.
/// </summary>
public sealed class PayUIndiaRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private readonly HttpClient _httpClient;
    private readonly PayUIndiaOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.PayUIndia;

    /// <summary>Create a new PayU India raw-card tokenisation provider.</summary>
    public PayUIndiaRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<PayUIndiaOptions> options,
        ILogger<PayUIndiaRawCardTokenisationProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.MerchantKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.MerchantKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.Salt))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PayUIndiaOptions.Salt)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.InfoBaseUrl ?? "https://info.payu.in/");
    }

    /// <inheritdoc />
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "tokenise");
        try
        {
            const string command = "save_user_card";
            var userCredentials = request.CustomerId ?? $"{_options.MerchantKey}:{request.Card.CardholderName}";
            var cardToken = $"card-{Guid.NewGuid():N}";

            // hash = SHA-512(key|command|var1|salt) — var1 = user_credentials
            var hashInput = string.Join("|", _options.MerchantKey, command, userCredentials, _options.Salt);
            var hash = PayUIndiaTokenisationProvider.Sha512Hex(hashInput);

            var form = new Dictionary<string, string>
            {
                ["key"] = _options.MerchantKey,
                ["command"] = command,
                ["var1"] = userCredentials,
                ["var2"] = request.Card.CardholderName,
                ["var3"] = request.Card.CardNumber,
                ["var4"] = request.Card.ExpiryMonth.ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                ["var5"] = request.Card.ExpiryYear.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["var6"] = cardToken,
                ["hash"] = hash
            };

            var raw = await PayUIndiaHttp.PostFormAsync(_httpClient, Logger, ProviderName, "merchant/postservice.php?form=2", form, ct, "TokeniseCard").ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<PayUIndiaTokenisationProvider.PayUIndiaCardResponse>(raw, PayUIndiaTokenisationProvider.DeserializeOptions);

            Logger.LogInformation("PayU India card vaulted: token={Token} customerId={CustomerId} status={Status}",
                response?.CardToken ?? cardToken, userCredentials, response?.Status);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            var last4 = request.Card.CardNumber.Length >= 4
                ? request.Card.CardNumber[^4..]
                : request.Card.CardNumber;

            return new PaymentMethod
            {
                Token = response?.CardToken ?? cardToken,
                CustomerId = userCredentials,
                Kind = PaymentMethodKind.Card,
                Brand = response?.CardMode,
                Last4 = last4,
                ExpiryMonth = request.Card.ExpiryMonth,
                ExpiryYear = request.Card.ExpiryYear,
                DisplayName = request.DisplayName,
                IsDefault = request.SetAsDefault,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }
}
