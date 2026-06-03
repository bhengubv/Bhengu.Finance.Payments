// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Razorpay.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Razorpay.Providers;

/// <summary>
/// Internal helper that wraps the Razorpay REST conventions — Basic-auth header, JSON serialisation,
/// status-code-to-exception translation, optional <c>X-Razorpay-IdempotencyKey</c> passthrough.
/// Re-used by every Razorpay provider so each one only contains the domain logic for its endpoint.
/// </summary>
internal sealed class RazorpayHttpClient
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly string _providerName;
    private readonly ILogger _logger;

    public RazorpayHttpClient(HttpClient httpClient, RazorpayOptions options, string providerName, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _providerName = providerName;
        _logger = logger;

        if (string.IsNullOrWhiteSpace(options.KeyId))
            throw new ProviderConfigurationException(providerName, $"{nameof(RazorpayOptions.KeyId)} is required");
        if (string.IsNullOrWhiteSpace(options.KeySecret))
            throw new ProviderConfigurationException(providerName, $"{nameof(RazorpayOptions.KeySecret)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(options.BaseUrl ?? "https://api.razorpay.com/");

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
        {
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.KeyId}:{options.KeySecret}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }
    }

    /// <summary>
    /// Send a JSON-body POST/PUT/PATCH. Adds <c>X-Razorpay-IdempotencyKey</c> when
    /// <paramref name="idempotencyKey"/> is non-empty.
    /// </summary>
    public Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        string operation,
        string? idempotencyKey = null)
        => SendCoreAsync(method, path, body, ct, operation, idempotencyKey);

    /// <summary>Send a GET. Convenience overload that emits no body.</summary>
    public Task<string> GetAsync(string path, CancellationToken ct, string operation)
        => SendCoreAsync(HttpMethod.Get, path, body: null, ct, operation, idempotencyKey: null);

    /// <summary>Send a DELETE. Convenience overload that emits no body.</summary>
    public Task<string> DeleteAsync(string path, CancellationToken ct, string operation)
        => SendCoreAsync(HttpMethod.Delete, path, body: null, ct, operation, idempotencyKey: null);

    private async Task<string> SendCoreAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct,
        string operation,
        string? idempotencyKey)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("X-Razorpay-IdempotencyKey", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(_providerName, "HTTP request to Razorpay failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(_providerName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Razorpay {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(_providerName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(_providerName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>Deserialise the response into <typeparamref name="T"/>, throwing a wrapped exception on failure.</summary>
    public static T DeserialiseOrThrow<T>(string raw, string providerName, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw)
                ?? throw new BhenguPaymentException(providerName, $"Razorpay {operation} returned empty body");
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(providerName, $"Failed to parse Razorpay {operation} response", innerException: ex);
        }
    }
}
