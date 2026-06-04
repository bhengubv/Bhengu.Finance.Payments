// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Pesapal.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Pesapal.Internals;

/// <summary>
/// Shared HTTP-call helper for every Pesapal sibling provider. Centralises Bearer-auth wiring,
/// JSON shaping and exception translation. Internal — consumers should depend on the typed
/// provider classes, not this helper.
/// </summary>
internal static class PesapalHttpClient
{
    /// <summary>Issue a Pesapal Bearer token via /api/Auth/RequestToken, cached for ~4.5 minutes.</summary>
    public static async Task<string> EnsureTokenAsync(
        HttpClient httpClient,
        ILogger logger,
        PesapalOptions options,
        PesapalTokenCache cache,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cache);

        var current = cache.Get();
        if (current is not null) return current;

        var body = new { consumer_key = options.ConsumerKey, consumer_secret = options.ConsumerSecret };
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/Auth/RequestToken")
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
            throw new ProviderUnavailableException(ProviderNames.Pesapal, "HTTP request to Pesapal failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Pesapal, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Pesapal, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Pesapal, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        var auth = JsonSerializer.Deserialize<PesapalAuthResponse>(responseBody);
        if (string.IsNullOrWhiteSpace(auth?.Token))
            throw new ProviderUnavailableException(ProviderNames.Pesapal, "Pesapal RequestToken returned an empty token");

        cache.Set(auth!.Token!);
        return auth.Token!;
    }

    /// <summary>Send a Bearer-authenticated JSON request and return the raw response body.</summary>
    public static async Task<string> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string path,
        object? body,
        string token,
        CancellationToken ct,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.Pesapal, "HTTP request to Pesapal failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Pesapal, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Pesapal {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Pesapal, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Pesapal, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class PesapalAuthResponse
    {
        [JsonPropertyName("token")] public string? Token { get; set; }
        [JsonPropertyName("expiryDate")] public string? ExpiryDate { get; set; }
    }
}
