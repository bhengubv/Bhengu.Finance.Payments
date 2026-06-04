// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.PayJustNow.Internals;

/// <summary>Shared HTTP helper for every PayJustNow sibling provider.</summary>
internal static class PayJustNowHttpClient
{
    /// <summary>Production default base URL.</summary>
    public const string ProductionDefaultUrl = "https://api.payjustnow.com/v1/";

    /// <summary>Sandbox default base URL.</summary>
    public const string SandboxDefaultUrl = "https://sandbox.payjustnow.com/v1/";

    /// <summary>Shared JsonSerializerOptions: PayJustNow uses snake_case throughout.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Apply BaseAddress + API-key headers to a freshly-resolved HttpClient.</summary>
    public static void ConfigureClient(HttpClient httpClient, PayJustNowOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (httpClient.BaseAddress is null)
        {
            httpClient.BaseAddress = new Uri(options.UseSandbox
                ? options.SandboxUrl ?? SandboxDefaultUrl
                : options.BaseUrl ?? ProductionDefaultUrl);
        }

        if (!httpClient.DefaultRequestHeaders.Contains("X-Api-Key"))
            httpClient.DefaultRequestHeaders.Add("X-Api-Key", options.ApiKey);
        if (!httpClient.DefaultRequestHeaders.Contains("X-Merchant-Id"))
            httpClient.DefaultRequestHeaders.Add("X-Merchant-Id", options.MerchantId);
    }

    /// <summary>
    /// Send an HTTP request, translate PayJustNow-style errors into the SDK exception taxonomy,
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
            throw new ProviderUnavailableException(ProviderNames.PayJustNow, "HTTP request to PayJustNow failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.PayJustNow, "HTTP request to PayJustNow timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.PayJustNow, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("PayJustNow {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.PayJustNow, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.PayJustNow, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
