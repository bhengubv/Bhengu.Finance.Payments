// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models.Vault;
using Bhengu.Finance.Payments.PagSeguro.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PagSeguro.Providers;

/// <summary>
/// PagSeguro / PagBank card-tokenisation provider. Wraps the <c>/tokens</c> endpoint —
/// server-side card vaulting that mints reusable card tokens scoped to the merchant.
/// </summary>
/// <remarks>
/// Server-side tokenisation transits raw PAN through your server — only use this provider if your
/// merchant is already PCI-DSS Level-1 SAQ-D. Otherwise prefer PagBank's client-side
/// JavaScript SDK which performs encryption client-side and returns an opaque encrypted card
/// blob your server passes through.
/// <para>
/// PagBank's <c>/tokens</c> endpoint returns a <c>card.id</c> the merchant uses on subsequent
/// orders. Listing, fetching, and deletion are operations on the merchant's vault; the SDK
/// surfaces them through this provider. PagBank does not expose a public list-by-customer
/// endpoint comparable to Stripe; <see cref="ListPaymentMethodsAsync"/> therefore returns an
/// empty list and logs a warning when the operation isn't supported by the current PagBank
/// release the SDK is calling against.
/// </para>
/// </remarks>
public sealed class PagSeguroTokenisationProvider : ITokenisationProvider
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly PagSeguroOptions _options;
    private readonly ILogger<PagSeguroTokenisationProvider> _logger;

    /// <inheritdoc />
    public string ProviderName => ProviderNames.PagSeguro;

    /// <summary>Create a new PagSeguro tokenisation provider bound to the supplied HTTP client and options.</summary>
    public PagSeguroTokenisationProvider(
        HttpClient httpClient,
        IOptions<PagSeguroOptions> options,
        ILogger<PagSeguroTokenisationProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ApiToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(PagSeguroOptions.ApiToken)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? (_options.SandboxUrl ?? "https://sandbox.api.pagseguro.com")
                : (_options.BaseUrl ?? "https://api.pagseguro.com");
            _httpClient.BaseAddress = new Uri(resolved);
        }

        if (_httpClient.DefaultRequestHeaders.Authorization is null)
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
    }

    /// <inheritdoc />
    public async Task<PaymentMethod> TokeniseAsync(TokeniseRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = new Dictionary<string, object?>
        {
            ["type"] = "card",
            ["card"] = new Dictionary<string, object?>
            {
                ["number"] = request.Card.CardNumber,
                ["exp_month"] = request.Card.ExpiryMonth,
                ["exp_year"] = request.Card.ExpiryYear,
                ["security_code"] = request.Card.Cvv,
                ["holder"] = new Dictionary<string, object?>
                {
                    ["name"] = request.Card.CardholderName
                }
            }
        };

        var raw = await SendAsync(HttpMethod.Post, "/tokens", body, ct, "Tokenise").ConfigureAwait(false);
        var token = DeserialiseOrThrow<PagSeguroToken>(raw, "Tokenise");

        _logger.LogInformation("PagSeguro token created: {Id}", token.Id);
        return MapToken(token, request.CustomerId, request.DisplayName, request.SetAsDefault);
    }

    /// <inheritdoc />
    public async Task<PaymentMethod?> GetPaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        try
        {
            var raw = await SendAsync(HttpMethod.Get, $"/tokens/{Uri.EscapeDataString(token)}", body: null, ct, "GetToken").ConfigureAwait(false);
            var t = DeserialiseOrThrow<PagSeguroToken>(raw, "GetToken");
            return MapToken(t, customerId: null, displayName: null, isDefault: false);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PaymentMethod>> ListPaymentMethodsAsync(string customerId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customerId);

        // PagBank's public v4 API does not currently expose a "list tokens by customer" endpoint.
        // Merchants persist their own (customerId, tokenId) mapping. Return an empty list rather
        // than throwing so consumers can swap providers without their UI breaking.
        _logger.LogWarning(
            "PagSeguro does not expose a list-by-customer endpoint. ListPaymentMethodsAsync returns empty; track (customer, token) mapping in your own datastore.");
        return Task.FromResult<IReadOnlyList<PaymentMethod>>(Array.Empty<PaymentMethod>());
    }

    /// <inheritdoc />
    public async Task<bool> DeletePaymentMethodAsync(string token, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        try
        {
            await SendAsync(HttpMethod.Delete, $"/tokens/{Uri.EscapeDataString(token)}", body: null, ct, "DeleteToken").ConfigureAwait(false);
            return true;
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return false;
        }
    }

    private static PaymentMethod MapToken(PagSeguroToken t, string? customerId, string? displayName, bool isDefault)
    {
        return new PaymentMethod
        {
            Token = t.Id ?? string.Empty,
            CustomerId = customerId,
            Kind = PaymentMethodKind.Card,
            Brand = t.Card?.Brand,
            Last4 = t.Card?.Last4,
            ExpiryMonth = t.Card?.ExpMonth,
            ExpiryYear = t.Card?.ExpYear,
            DisplayName = displayName,
            IsDefault = isDefault,
            CreatedAt = TryParseDate(t.CreatedAt)
        };
    }

    private static DateTime? TryParseDate(string? value) =>
        DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : (DateTime?)null;

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, WriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to PagSeguro failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("PagSeguro {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static T DeserialiseOrThrow<T>(string raw, string operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(raw)
                ?? throw new BhenguPaymentException(ProviderNames.PagSeguro, $"PagSeguro {operation} returned empty body");
        }
        catch (JsonException ex)
        {
            throw new BhenguPaymentException(ProviderNames.PagSeguro, $"Failed to parse PagSeguro {operation} response", innerException: ex);
        }
    }

    // === PagSeguro API response shapes (internal) ===

    private sealed class PagSeguroToken
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("card")] public PagSeguroTokenCard? Card { get; set; }
    }

    private sealed class PagSeguroTokenCard
    {
        [JsonPropertyName("brand")] public string? Brand { get; set; }
        [JsonPropertyName("first6")] public string? First6 { get; set; }
        [JsonPropertyName("last4")] public string? Last4 { get; set; }
        [JsonPropertyName("exp_month")] public int? ExpMonth { get; set; }
        [JsonPropertyName("exp_year")] public int? ExpYear { get; set; }
    }
}
