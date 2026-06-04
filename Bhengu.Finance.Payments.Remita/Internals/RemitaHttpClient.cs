// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Remita.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Remita.Internals;

/// <summary>
/// Shared HTTP-call helper for Remita's auxiliary providers (mandate / settlement). Centralises
/// Remita's hash-based authentication (SHA-512 over concatenated fields + ApiKey), JSON shaping,
/// error translation and logging. Internal — consumers depend on the typed provider classes.
/// </summary>
internal sealed class RemitaHttpClient
{
    /// <summary>Default production base URL when <see cref="RemitaOptions.BaseUrl"/> is unset.</summary>
    public const string DefaultProductionUrl = "https://login.remita.net";

    /// <summary>Default sandbox base URL when <see cref="RemitaOptions.SandboxUrl"/> is unset.</summary>
    public const string DefaultSandboxUrl = "https://remitademo.net";

    /// <summary>JSON serializer options for Remita payloads.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly RemitaOptions _options;
    private readonly ILogger _logger;

    /// <summary>Construct a helper, binding it to the supplied <see cref="HttpClient"/> and options.</summary>
    public RemitaHttpClient(HttpClient httpClient, RemitaOptions options, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? DefaultSandboxUrl
                : _options.BaseUrl ?? DefaultProductionUrl;
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
        }
    }

    /// <summary>
    /// Send an HTTP request to Remita, translate provider-specific errors into the SDK exception
    /// taxonomy, and return the raw response body.
    /// </summary>
    /// <param name="authHash">Pre-computed SHA-512 hash used as <c>remitaConsumerToken</c>.</param>
    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string operation,
        string authHash,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Json);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        var authHeader = $"remitaConsumerKey={_options.MerchantId},remitaConsumerToken={authHash}";
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.Remita, "HTTP request to Remita failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.Remita, "HTTP request to Remita timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Remita, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Remita {Operation} failed: {StatusCode} {Body}",
                operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Remita,
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Remita,
                $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>
    /// Compute Remita's standard SHA-512 hex hash over the supplied concatenated input. Used to
    /// build the <c>remitaConsumerToken</c> value carried in the Authorization header.
    /// </summary>
    public static string Sha512Hex(string input)
    {
        var bytes = SHA512.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
