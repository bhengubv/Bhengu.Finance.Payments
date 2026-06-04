// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Paymob.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Paymob.Internals;

/// <summary>
/// Shared HTTP helper for every Paymob sibling provider. Centralises base-URL wiring, JSON
/// shaping, error translation (rate-limit / declined / unavailable) and logging. Internal —
/// consumers should depend on the typed provider classes, not this helper.
/// </summary>
internal static class PaymobHttpClient
{
    /// <summary>Default base URL when <see cref="PaymobOptions.BaseUrl"/> is unset.</summary>
    public const string DefaultBaseUrl = "https://accept.paymob.com/";

    /// <summary>Shared JsonSerializerOptions: Paymob uses snake_case throughout.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Apply BaseAddress to a freshly-resolved HttpClient if not already configured.</summary>
    public static void ConfigureClient(HttpClient httpClient, PaymobOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (httpClient.BaseAddress is null)
            httpClient.BaseAddress = new Uri(options.BaseUrl ?? DefaultBaseUrl);
    }

    /// <summary>
    /// Send an HTTP request, translate Paymob-style errors into the SDK exception taxonomy,
    /// and return the raw response body.
    /// </summary>
    public static async Task<string> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string path,
        object? body,
        string operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Json);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.Paymob, "HTTP request to Paymob failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.Paymob, "HTTP request to Paymob timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Paymob, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Paymob {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Paymob, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Paymob, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// Exchange the merchant API key for a short-lived auth_token. Paymob auth tokens are valid
    /// for one hour; we leave caching to the per-provider call-site to keep this helper stateless.
    /// </summary>
    public static async Task<string> AuthenticateAsync(
        HttpClient httpClient,
        ILogger logger,
        PaymobOptions options,
        CancellationToken ct)
    {
        var body = new { api_key = options.ApiKey };
        var json = await SendAsync(httpClient, logger, HttpMethod.Post, "api/auth/tokens", body, "Authenticate", ct).ConfigureAwait(false);
        var token = JsonSerializer.Deserialize<AuthResponse>(json, Json)?.Token;
        if (string.IsNullOrEmpty(token))
            throw new ProviderUnavailableException(ProviderNames.Paymob, "Paymob auth returned no token");
        return token;
    }

    private sealed class AuthResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("token")]
        public string? Token { get; set; }
    }
}
