// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.ExpressPay.Internals;

/// <summary>Shared form-urlencoded HTTP helper for every ExpressPay sibling provider.</summary>
internal static class ExpressPayHttpClient
{
    /// <summary>Send a form-encoded request and return the raw response body, translating ExpressPay errors into the SDK exception taxonomy.</summary>
    public static async Task<string> SendFormAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string path,
        Dictionary<string, string> form,
        CancellationToken ct,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        using var req = new HttpRequestMessage(method, path)
        {
            Content = new FormUrlEncodedContent(form)
        };

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.ExpressPay, "HTTP request to ExpressPay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.ExpressPay, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("ExpressPay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.ExpressPay, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.ExpressPay, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
