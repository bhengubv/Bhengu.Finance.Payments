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
/// Shared Monnify HTTP helper (Moniepoint's developer API is Monnify). Handles the
/// Basic-auth → <c>POST /api/v1/auth/login</c> → Bearer-token flow (token cached for its lifetime),
/// JSON shaping, and error translation. Returns raw response bodies; callers deserialize the Monnify
/// <c>{requestSuccessful, responseBody}</c> envelope via <see cref="MonnifyEnvelope{T}"/>. Internal.
/// </summary>
internal sealed class MoniepointHttpClient
{
    public const string LiveBaseUrl = "https://api.monnify.com/";
    public const string SandboxBaseUrl = "https://sandbox.monnify.com/";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly MoniepointOptions _options;
    private readonly ILogger _logger;

    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public MoniepointHttpClient(HttpClient httpClient, MoniepointOptions options, ILogger logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? (_options.UseSandbox ? SandboxBaseUrl : LiveBaseUrl));
    }

    /// <summary>Send an authenticated (Bearer) request to Monnify and return the raw response body.</summary>
    public async Task<string> SendAsync(HttpMethod method, string path, object? body, string operation, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");

        return await SendRawAsync(req, operation, ct).ConfigureAwait(false);
    }

    /// <summary>Obtain (and cache) a Monnify access token via Basic auth on <c>/api/v1/auth/login</c>.</summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return _token;

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ApiKey}:{_options.SecretKey}"));
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        var body = await SendRawAsync(req, "Login", ct).ConfigureAwait(false);
        var env = JsonSerializer.Deserialize<MonnifyEnvelope<LoginBody>>(body, Json);
        if (env?.RequestSuccessful != true || string.IsNullOrEmpty(env.ResponseBody?.AccessToken))
            throw new ProviderUnavailableException(ProviderNames.Moniepoint, $"Monnify login failed: {env?.ResponseMessage ?? body}");

        _token = env.ResponseBody.AccessToken;
        // Refresh shortly before the stated expiry to avoid edge-of-expiry races.
        var seconds = env.ResponseBody.ExpiresIn > 60 ? env.ResponseBody.ExpiresIn - 30 : env.ResponseBody.ExpiresIn;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return _token;
    }

    private async Task<string> SendRawAsync(HttpRequestMessage req, string operation, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderNames.Moniepoint, "HTTP request to Monnify failed", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ProviderUnavailableException(ProviderNames.Moniepoint, "HTTP request to Monnify timed out", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderNames.Moniepoint, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Monnify {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderNames.Moniepoint, ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderNames.Moniepoint, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    /// <summary>Every Monnify response is wrapped in this envelope.</summary>
    internal sealed class MonnifyEnvelope<T>
    {
        public bool RequestSuccessful { get; set; }
        public string? ResponseMessage { get; set; }
        public string? ResponseCode { get; set; }
        public T? ResponseBody { get; set; }
    }

    private sealed class LoginBody
    {
        public string? AccessToken { get; set; }
        public int ExpiresIn { get; set; }
    }
}
