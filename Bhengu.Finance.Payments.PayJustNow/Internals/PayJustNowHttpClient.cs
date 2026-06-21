// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.PayJustNow.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.PayJustNow.Internals;

/// <summary>
/// Shared HTTP helper for the PayJustNow merchant API.
/// <para>
/// Wire format verified against PayJustNow's official, public production source:
/// the PayJustNow-for-WooCommerce gateway (WordPress.org plugin SVN, stable tag 2.7.9,
/// <c>classes/payjustnow.class.php</c>) and the PayJustNow public API README
/// (github.com/PayJustNow/Api). Both confirm: base path <c>/api/v1/merchant/</c>,
/// HTTP Basic authentication, and JSON request/response bodies.
/// </para>
/// </summary>
internal static class PayJustNowHttpClient
{
    // Source: payjustnow.class.php L557 — 'https://api.payjustnow.com/api/v1/merchant/checkout'.
    /// <summary>Production default base URL (includes the <c>/api/v1/merchant/</c> path prefix).</summary>
    public const string ProductionDefaultUrl = "https://api.payjustnow.com/api/v1/merchant/";

    // Source: payjustnow.class.php L554 — 'https://sandbox.payjustnow.com/api/v1/merchant/checkout'.
    /// <summary>Sandbox default base URL (includes the <c>/api/v1/merchant/</c> path prefix).</summary>
    public const string SandboxDefaultUrl = "https://sandbox.payjustnow.com/api/v1/merchant/";

    /// <summary>Shared JsonSerializerOptions: PayJustNow uses snake_case throughout.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Apply BaseAddress + HTTP Basic auth to a freshly-resolved HttpClient.
    /// <para>
    /// Auth scheme verified from payjustnow.class.php L533/L725:
    /// <c>'Basic ' . base64_encode($merchant_id . ':' . $merchant_api_key)</c> — i.e. the Merchant ID
    /// is the Basic username and the Merchant API Key is the Basic password
    /// (settings fields "Merchant ID" → <c>pjn_username</c>, "Merchant API Key" → <c>pjn_password</c>,
    /// L271–L283). The public API README documents the same scheme with test credentials <c>1:secret</c>.
    /// </para>
    /// </summary>
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

        if (httpClient.DefaultRequestHeaders.Authorization is null)
        {
            var raw = $"{options.MerchantId}:{options.ApiKey}";
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        if (!httpClient.DefaultRequestHeaders.Accept.Any())
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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
