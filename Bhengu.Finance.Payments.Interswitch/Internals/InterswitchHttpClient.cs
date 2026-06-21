// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Interswitch.Configuration;
using Microsoft.Extensions.Logging;

namespace Bhengu.Finance.Payments.Interswitch.Internals;

/// <summary>
/// Shared HTTP-call helper for Interswitch's auxiliary providers (tokenisation / settlement / etc.).
/// Centralises OAuth2 token caching, Interswitch's SHA-512/Base64 request-signature scheme, JSON shaping,
/// error translation and logging. Internal — consumers depend on the typed provider classes,
/// not this helper. The main <c>InterswitchPaymentProvider</c> keeps its own equivalent logic
/// to avoid breaking external behaviour; this helper exists for the new contract classes.
/// </summary>
internal sealed class InterswitchHttpClient
{
    /// <summary>Default production base URL when <see cref="InterswitchOptions.BaseUrl"/> is unset.</summary>
    /// <remarks>Source: https://docs.interswitchgroup.com/docs/authentication (production OAuth host <c>passport.interswitchng.com</c>).</remarks>
    public const string DefaultProductionUrl = "https://passport.interswitchng.com";

    /// <summary>Default sandbox base URL when <see cref="InterswitchOptions.SandboxUrl"/> is unset.</summary>
    /// <remarks>
    /// Source: https://sandbox.interswitchng.com/docbase/docs/access-token-request/get-access-token/ and
    /// https://docs.interswitchgroup.com/docs/authentication — the sandbox passport host is
    /// <c>sandbox.interswitchng.com</c> (the legacy <c>qa.interswitchng.com</c> host was incorrect).
    /// </remarks>
    public const string DefaultSandboxUrl = "https://sandbox.interswitchng.com";

    /// <summary>JSON serializer options for Interswitch payloads.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly InterswitchOptions _options;
    private readonly ILogger _logger;

    private string? _cachedAccessToken;
    private DateTime _accessTokenExpiresAtUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>Construct a helper, binding it to the supplied <see cref="HttpClient"/> and options.</summary>
    public InterswitchHttpClient(HttpClient httpClient, InterswitchOptions options, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
        {
            var baseUrl = _options.UseSandbox
                ? _options.SandboxUrl ?? DefaultSandboxUrl
                : _options.BaseUrl ?? DefaultProductionUrl;
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            _httpClient.BaseAddress = new Uri(baseUrl);
        }
    }

    /// <summary>
    /// Send an HTTP request to Interswitch, translate provider-specific errors into the SDK
    /// exception taxonomy, and return the raw response body.
    /// </summary>
    public async Task<string> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        string operation,
        CancellationToken ct)
    {
        var token = await EnsureAccessTokenAsync(ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, Json);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        // Interswitch "InterswitchAuth" security headers. Verified scheme (see InterswitchSignature):
        //   signature = Base64( SHA512( method & percent_encode(absoluteUrl) & timestampSeconds & nonce & clientId & clientSecret ) )
        //   timestamp = Unix SECONDS (NOT milliseconds); SignatureMethod header = "SHA512".
        // Sources: https://sandbox.interswitchng.com/docbase/docs/interswitch-sec-headers/sample-code/
        //          https://interswitch-docs.readme.io/reference/header-computation
        var timestampSeconds = InterswitchSignature.UnixTimestampSeconds();
        var nonce = Guid.NewGuid().ToString("N");
        var resourceUrl = BuildSignedResourceUrl(path);
        var signature = InterswitchSignature.ComputeSignature(
            method.Method, resourceUrl, timestampSeconds, nonce, _options.ClientId, _options.ClientSecret);

        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("Signature", signature);
        req.Headers.TryAddWithoutValidation("SignatureMethod", InterswitchSignature.SignatureMethod);
        req.Headers.TryAddWithoutValidation("Timestamp", timestampSeconds);
        req.Headers.TryAddWithoutValidation("Nonce", nonce);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.Interswitch, "HTTP request to Interswitch failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.Interswitch, "HTTP request to Interswitch timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Interswitch, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Interswitch {Operation} failed: {StatusCode} {Body}",
                operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Interswitch,
                    ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Interswitch,
                $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private async Task<string> EnsureAccessTokenAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTime.UtcNow < _accessTokenExpiresAtUtc.AddSeconds(-30))
            return _cachedAccessToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!string.IsNullOrEmpty(_cachedAccessToken) && DateTime.UtcNow < _accessTokenExpiresAtUtc.AddSeconds(-30))
                return _cachedAccessToken;

            using var req = new HttpRequestMessage(HttpMethod.Post, "passport/oauth/token");
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            req.Content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("scope", "profile")
            });

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderNames.Interswitch, "Interswitch token endpoint unreachable", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Interswitch OAuth2 token failed: {StatusCode} {Body}", response.StatusCode, body);
                throw new ProviderUnavailableException(ProviderNames.Interswitch,
                    $"OAuth2 token HTTP {(int)response.StatusCode}: {body}");
            }

            var token = JsonSerializer.Deserialize<InterswitchTokenResponse>(body, Json);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderNames.Interswitch,
                    "Interswitch OAuth2 token response missing access_token");

            _cachedAccessToken = token.AccessToken;
            _accessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600);
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>
    /// Build the percent-encoded ABSOLUTE resource URL that Interswitch signs. The documented
    /// signature cipher uses <c>percent_encode(url)</c> over the full URL (scheme + host + path),
    /// not just the path. Source: https://interswitch-docs.readme.io/reference/header-computation
    /// </summary>
    private string BuildSignedResourceUrl(string path)
    {
        var relative = path.StartsWith('/') ? path[1..] : path;
        var absolute = _httpClient.BaseAddress is { } baseAddress
            ? new Uri(baseAddress, relative).AbsoluteUri
            : (relative.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? relative : "/" + relative);
        return Uri.EscapeDataString(absolute);
    }

    private sealed class InterswitchTokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
