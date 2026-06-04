// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.OPay.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.OPay.Internals;

/// <summary>
/// Shared HTTP-call helper for OPay's auxiliary providers (tokenisation / settlement). Centralises
/// HMAC-SHA512 signing, JSON shaping, error translation and logging. Internal — consumers depend
/// on the typed provider classes, not this helper.
/// </summary>
internal sealed class OPayHttpClient
{
    /// <summary>Default production base URL when <see cref="OPayOptions.BaseUrl"/> is unset.</summary>
    public const string DefaultProductionUrl = "https://liveapi.opaycheckout.com";

    /// <summary>Default sandbox base URL when <see cref="OPayOptions.SandboxUrl"/> is unset.</summary>
    public const string DefaultSandboxUrl = "https://sandboxapi.opaycheckout.com";

    /// <summary>JSON serializer options for OPay payloads.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly OPayOptions _options;
    private readonly ILogger _logger;

    /// <summary>Construct a helper, binding it to the supplied <see cref="HttpClient"/> and options.</summary>
    public OPayHttpClient(HttpClient httpClient, OPayOptions options, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
        {
            var baseUrl = _options.UseSandbox
                ? _options.SandboxUrl ?? DefaultSandboxUrl
                : _options.BaseUrl ?? DefaultProductionUrl;
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    /// <summary>
    /// Send an HTTP request to OPay, translate provider-specific errors into the SDK exception
    /// taxonomy, and return the raw response body.
    /// </summary>
    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string operation,
        CancellationToken ct)
    {
        var json = body is null ? string.Empty : JsonSerializer.Serialize(body, Json);
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(_options.SecretKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", signature);
        req.Headers.TryAddWithoutValidation("MerchantId", _options.MerchantId);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.OPay, "HTTP request to OPay failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.OPay, "HTTP request to OPay timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.OPay, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("OPay {Operation} failed: {StatusCode} {Body}",
                operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.OPay,
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.OPay,
                $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
