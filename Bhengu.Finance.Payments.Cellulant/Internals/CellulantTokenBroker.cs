// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Cellulant.Configuration;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.Cellulant.Internals;

/// <summary>
/// Process-wide singleton that mints and caches Cellulant (Tingg) OAuth2 access tokens.
/// Shared by every Cellulant provider so a single bearer is reused across the family rather
/// than each provider running its own token-refresh loop. Tokens are valid for 3600s by default;
/// renewal happens 60s before expiry under a mutex.
/// </summary>
public sealed class CellulantTokenBroker
{
    private readonly CellulantOptions _options;
    private readonly ILogger<CellulantTokenBroker> _logger;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>Construct the broker. Designed to be registered as a DI singleton.</summary>
    public CellulantTokenBroker(IOptions<CellulantOptions> options, ILogger<CellulantTokenBroker> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Return a non-expired bearer token, minting a new one against the OAuth endpoint if the
    /// cached value is missing or within 60 seconds of expiry.
    /// </summary>
    public async Task<string> EnsureAccessTokenAsync(HttpClient httpClient, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - TimeSpan.FromMinutes(1))
            return _accessToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _accessTokenExpiresAt - TimeSpan.FromMinutes(1))
                return _accessToken;

            // Verified: POST {host}/v1/oauth/token/request with body {client_id, client_secret,
            // grant_type:"client_credentials"} and the apiKey carried in the `apikey` header.
            // Source: https://docs.tingg.africa/reference/authenticate-requests
            var requestBody = new
            {
                grant_type = "client_credentials",
                client_id = _options.ClientId,
                client_secret = _options.ClientSecret
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/oauth/token/request")
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
            };
            // Tingg names this header `apikey` (lowercase) on the token endpoint.
            // Source: https://docs.tingg.africa/reference/authenticate-requests
            if (!string.IsNullOrEmpty(_options.ApiKey))
                req.Headers.TryAddWithoutValidation("apikey", _options.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderNames.Cellulant, "HTTP request to Cellulant OAuth failed", ex);
            }

            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderUnavailableException(ProviderNames.Cellulant, $"Cellulant OAuth returned {(int)response.StatusCode}: {responseBody}");

            var tokenResponse = JsonSerializer.Deserialize<CellulantTokenResponse>(responseBody);
            if (tokenResponse is null || string.IsNullOrEmpty(tokenResponse.AccessToken))
                throw new ProviderUnavailableException(ProviderNames.Cellulant, "Cellulant OAuth returned no access_token");

            _accessToken = tokenResponse.AccessToken;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn > 0 ? tokenResponse.ExpiresIn : 3600);
            _logger.LogDebug("Cellulant OAuth token minted; expires {Expiry}", _accessTokenExpiresAt);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private sealed class CellulantTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }
}
