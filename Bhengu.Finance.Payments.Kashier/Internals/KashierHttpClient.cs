// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Kashier.Internals;

/// <summary>Shared HTTP helper for every Kashier sibling provider.</summary>
internal static class KashierHttpClient
{
    /// <summary>Default base URL when <see cref="KashierOptions.BaseUrl"/> is unset.</summary>
    public const string DefaultBaseUrl = "https://api.kashier.io/";

    /// <summary>Shared JsonSerializerOptions: Kashier uses camelCase throughout.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Apply BaseAddress + Authorization header to a freshly-resolved HttpClient if not already configured.</summary>
    public static void ConfigureClient(HttpClient httpClient, KashierOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (httpClient.BaseAddress is null)
            httpClient.BaseAddress = new Uri(options.BaseUrl ?? DefaultBaseUrl);

        if (httpClient.DefaultRequestHeaders.Authorization is null)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(options.ApiKey);
    }

    /// <summary>
    /// Send an HTTP request, translate Kashier-style errors into the SDK exception taxonomy,
    /// and return the raw response body.
    /// </summary>
    public static async Task<string> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string path,
        object? body,
        string operation,
        CancellationToken ct,
        string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Json);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.Kashier, "HTTP request to Kashier failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.Kashier, "HTTP request to Kashier timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Kashier, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Kashier {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Kashier, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Kashier, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
