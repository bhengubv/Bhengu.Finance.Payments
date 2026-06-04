// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Text;
using Bhengu.Finance.Payments.CMI.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.CMI.Internals;

/// <summary>Shared HTTP helper for every CMI sibling provider.</summary>
internal static class CMIHttpClient
{
    /// <summary>Default live base URL when <see cref="CMIOptions.BaseUrl"/> is unset.</summary>
    public const string LiveDefaultUrl = "https://payment.cmi.co.ma/";

    /// <summary>Default sandbox base URL when <see cref="CMIOptions.SandboxUrl"/> is unset.</summary>
    public const string SandboxDefaultUrl = "https://testpayment.cmi.co.ma/";

    /// <summary>Apply BaseAddress to a freshly-resolved HttpClient if not already configured.</summary>
    public static void ConfigureClient(HttpClient httpClient, CMIOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        if (httpClient.BaseAddress is null)
        {
            var url = options.UseSandbox
                ? options.SandboxUrl ?? SandboxDefaultUrl
                : options.BaseUrl ?? LiveDefaultUrl;
            httpClient.BaseAddress = new Uri(url);
        }
    }

    /// <summary>
    /// POST a form-urlencoded body (CMI's <c>fim/api</c> wraps XML inside the <c>DATA</c> form
    /// field, and the settlement-feed endpoint takes plain form fields). Translates errors into
    /// the SDK taxonomy.
    /// </summary>
    public static async Task<string> SendFormAsync(
        HttpClient httpClient,
        ILogger logger,
        string path,
        IDictionary<string, string> form,
        string operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(form);

        using var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form)
        };

        return await ExecuteAsync(httpClient, logger, req, operation, ct).ConfigureAwait(false);
    }

    /// <summary>GET a path with raw query string. Translates errors into the SDK taxonomy.</summary>
    public static async Task<string> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string path,
        string? body,
        string operation,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        return await ExecuteAsync(httpClient, logger, req, operation, ct).ConfigureAwait(false);
    }

    private static async Task<string> ExecuteAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpRequestMessage req,
        string operation,
        CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.CMI, "HTTP request to CMI failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.CMI, "HTTP request to CMI timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.CMI, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("CMI {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.CMI, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.CMI, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
