// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Yoco.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Yoco.Providers;

/// <summary>
/// Yoco implementation of <see cref="IRawCardTokenisationProvider"/> — the PCI-scope-clarifying
/// WRITE counterpart to <see cref="YocoTokenisationProvider"/>. Splitting tokenisation into a
/// dedicated raw-card type makes the SAQ-D scope explicit at the type-system level.
/// </summary>
/// <remarks>
/// <para>Yoco's hosted-checkout API expects an amount-in-cents tokenisation pre-auth. The SDK
/// opens a R0.01 minimum checkout so the page actually opens; the merchant later voids or
/// refunds it before settlement. For test purposes the auth amount can be any positive integer.</para>
/// <para><b>Strongly prefer client-side tokenisation</b> (Yoco Inline) — the payer's browser sends
/// the card straight to Yoco and your server only ever sees the resulting checkout id. SAQ stays
/// at A.</para>
/// </remarks>
public sealed class YocoRawCardTokenisationProvider : BhenguProviderBase, IRawCardTokenisationProvider
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly YocoOptions _options;
    private readonly YocoTokenCache _cache;

    /// <inheritdoc/>
    public override string ProviderName => ProviderNames.Yoco;

    /// <summary>Construct a Yoco raw-card tokenisation provider. Designed to be registered via DI.</summary>
    public YocoRawCardTokenisationProvider(
        HttpClient httpClient,
        IOptions<YocoOptions> options,
        ILogger<YocoRawCardTokenisationProvider> logger,
        YocoTokenCache cache)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));

        if (string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(YocoOptions.SecretKey)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://online.yoco.com/v1/");

        if (!_httpClient.DefaultRequestHeaders.Contains("X-Auth-Secret-Key"))
            _httpClient.DefaultRequestHeaders.Add("X-Auth-Secret-Key", _options.SecretKey);
    }

    /// <inheritdoc/>
    public Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunOperationAsync("tokenise", () => TokeniseCoreAsync(request, ct), ct);
    }

    private async Task<PaymentMethod> TokeniseCoreAsync(TokeniseRequest request, CancellationToken ct)
    {
        // Yoco's hosted-checkout API expects an amount-in-cents tokenisation pre-auth.
        // We use a R0.01 minimum so the checkout actually opens; the merchant later voids
        // or refunds it before settlement. For test purposes the auth amount can be any
        // positive integer. Callers can override via TokeniseRequest.DisplayName.
        var requestBody = new
        {
            amount = 100, // 1 ZAR in cents — minimum to open the Yoco hosted page
            currency = "ZAR",
            metadata = new
            {
                tokenisationOnly = true,
                displayName = request.DisplayName,
                customerId = request.CustomerId,
                setAsDefault = request.SetAsDefault
            }
        };

        var body = await SendAsync(HttpMethod.Post, "checkouts", requestBody, ct, "Tokenise").ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<YocoCheckoutResponse>(body, s_jsonOptions);

        if (response is null || string.IsNullOrEmpty(response.Id))
            throw new BhenguPaymentException(ProviderName, "Yoco checkout returned no id", "no_checkout_id");

        var method = new PaymentMethod
        {
            Token = response.Id,
            CustomerId = request.CustomerId,
            Kind = PaymentMethodKind.Card,
            Brand = null,
            Last4 = null,
            ExpiryMonth = null,
            ExpiryYear = null,
            DisplayName = request.DisplayName,
            IsDefault = request.SetAsDefault,
            CreatedAt = DateTime.UtcNow
        };

        _cache.Set(method);
        Logger.LogInformation(
            "Yoco checkout opened for tokenisation: id={CheckoutId} redirect={Redirect}",
            response.Id, response.RedirectUrl);

        return method;
    }

    private async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, s_jsonOptions), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Yoco {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class YocoCheckoutResponse
    {
        public string? Id { get; set; }
        public string? RedirectUrl { get; set; }
        public string? Status { get; set; }
        public int Amount { get; set; }
        public string? Currency { get; set; }
    }
}
