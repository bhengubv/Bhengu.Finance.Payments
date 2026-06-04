// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Moniepoint.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Moniepoint.Internals;

/// <summary>
/// Shared HTTP-call helper for Moniepoint's auxiliary providers (settlement). Centralises bearer
/// auth, JSON shaping, error translation and logging. Internal — consumers depend on the typed
/// provider classes, not this helper.
/// </summary>
internal sealed class MoniepointHttpClient
{
    /// <summary>Default base URL when <see cref="MoniepointOptions.BaseUrl"/> is unset.</summary>
    public const string DefaultBaseUrl = "https://api.moniepoint.com/";

    /// <summary>JSON serializer options for Moniepoint payloads.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly MoniepointOptions _options;
    private readonly ILogger _logger;

    /// <summary>Construct a helper, binding it to the supplied <see cref="HttpClient"/> and options.</summary>
    public MoniepointHttpClient(HttpClient httpClient, MoniepointOptions options, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? DefaultBaseUrl);

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
    }

    /// <summary>
    /// Send an HTTP request to Moniepoint, translate provider-specific errors into the SDK
    /// exception taxonomy, and return the raw response body.
    /// </summary>
    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string operation,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Json);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.Moniepoint, "HTTP request to Moniepoint failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.Moniepoint, "HTTP request to Moniepoint timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Moniepoint, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Moniepoint {Operation} failed: {StatusCode} {Body}",
                operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Moniepoint,
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Moniepoint,
                $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }
}
