// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Kashier.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Kashier.Internals;

/// <summary>Shared HTTP helper for every Kashier sibling provider.</summary>
/// <remarks>
/// Base URLs and auth are taken from Kashier's documentation and the asciisd/kashier SDK:
/// <list type="bullet">
///   <item>REST base — <c>https://test-api.kashier.io</c> (sandbox) / <c>https://api.kashier.io</c> (live).
///         Source: asciisd KashierConstants (<c>REST_SANDBOX_ENDPOINT</c>/<c>REST_LIVE_ENDPOINT</c>) and the
///         developers.kashier.io order-reconciliation curl example.</item>
///   <item>Auth — the raw Secret Key in the <c>Authorization</c> header (no scheme prefix).
///         Source: asciisd Refund.php (<c>Authorization =&gt; getSecretKey()</c>) and the reconciliation page
///         (<c>-H 'Authorization: your_secretKey'</c>).</item>
/// </list>
/// </remarks>
internal static class KashierHttpClient
{
    /// <summary>REST base URL for the Kashier sandbox.</summary>
    public const string SandboxBaseUrl = "https://test-api.kashier.io/";

    /// <summary>REST base URL for Kashier live.</summary>
    public const string LiveBaseUrl = "https://api.kashier.io/";

    /// <summary>Default hosted-payment-page / iframe base URL.</summary>
    public const string DefaultHostedPaymentBaseUrl = "https://checkout.kashier.io";

    /// <summary>Resolve the REST base URL from options (explicit override wins, else sandbox/live by flag).</summary>
    public static string ResolveBaseUrl(KashierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            return options.BaseUrl;
        return options.UseSandbox ? SandboxBaseUrl : LiveBaseUrl;
    }

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
            httpClient.BaseAddress = new Uri(ResolveBaseUrl(options));

        // Kashier expects the Secret Key as the raw Authorization header value (no "Bearer"/scheme prefix).
        if (!httpClient.DefaultRequestHeaders.Contains("Authorization") && !string.IsNullOrWhiteSpace(options.SecretKey))
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", options.SecretKey);
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
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
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
