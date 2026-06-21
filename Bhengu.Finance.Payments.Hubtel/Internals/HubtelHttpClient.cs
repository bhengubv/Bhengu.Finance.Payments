// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Hubtel.Internals;

/// <summary>
/// Shared HTTP-call helper for every Hubtel sibling provider. Centralises JSON shaping
/// and the exception-translation pattern (rate-limit / declined / unavailable).
/// <para>
/// The <c>path</c> argument may be an absolute URI. Hubtel splits its surface across two hosts
/// (payproxyapi.hubtel.com for Online Checkout, api.hubtel.com for Merchant Account), so providers
/// pass per-operation absolute URLs rather than relying on a single <see cref="HttpClient.BaseAddress"/>.
/// </para>
/// </summary>
internal static class HubtelHttpClient
{
    /// <summary>
    /// Serializer options shared by every Hubtel provider. Hubtel mixes casings across products —
    /// Online Checkout uses camelCase, Merchant Account / callbacks use PascalCase — so reads are
    /// case-insensitive; writes drop nulls to keep request bodies tight.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Send a JSON request and return the raw response body, translating Hubtel errors into the SDK exception taxonomy.</summary>
    public static async Task<string> SendAsync(
        HttpClient httpClient,
        ILogger logger,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        string operation)
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
            throw new ProviderUnavailableException(ProviderNames.Hubtel, "HTTP request to Hubtel failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Hubtel, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Hubtel {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Hubtel, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Hubtel, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
