// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Paystack.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Paystack.Internals;

/// <summary>
/// Shared HTTP-call helper for every Paystack provider. Centralises bearer-auth wiring,
/// JSON shaping, error translation (rate-limit / declined / unavailable) and logging.
/// Internal — consumers should depend on the typed provider classes, not this helper.
/// </summary>
internal static class PaystackHttpClient
{
    /// <summary>Default base URL when <see cref="PaystackOptions.BaseUrl"/> is unset.</summary>
    public const string DefaultBaseUrl = "https://api.paystack.co/";

    /// <summary>Lazy serializer options — Paystack uses snake_case everywhere.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Apply BaseAddress + Bearer auth to a freshly-resolved HttpClient if not already configured.</summary>
    public static void ConfigureClient(HttpClient httpClient, PaystackOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (httpClient.BaseAddress is null)
            httpClient.BaseAddress = new Uri(options.BaseUrl ?? DefaultBaseUrl);

        if (httpClient.DefaultRequestHeaders.Authorization is null)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
    }

    /// <summary>
    /// Send an HTTP request, translate Paystack-style errors into the SDK exception taxonomy,
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
            throw new ProviderUnavailableException(ProviderNames.Paystack, "HTTP request to Paystack failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.Paystack, "HTTP request to Paystack timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Paystack, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Paystack {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Paystack, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Paystack, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
