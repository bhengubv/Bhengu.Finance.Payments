// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Mandate;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.Stitch.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mandate = Bhengu.Finance.Payments.Core.Models.Mandate.Mandate;

namespace Bhengu.Finance.Payments.Stitch.Providers;

/// <summary>
/// Stitch implementation of <see cref="IMandateProvider"/> — wraps Stitch's DebiCheck
/// payment-initiation rails (paymentInitiation + paymentInitiationDebit) under the unified
/// Bhengu mandate contract.
/// </summary>
/// <remarks>
/// <para>
/// Stitch DebiCheck uses a redirect-based authorisation flow:
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="CreateMandateAsync"/> issues the <c>createPaymentInitiation</c> mutation with
///     <c>debiCheck</c> input — the response carries a payer-authorisation URL the merchant
///     redirects the customer to. The customer approves via their bank's app
///     (TymeBank, ABSA, FNB, Standard Bank, Capitec all support DebiCheck).
///   </description></item>
///   <item><description>
///     Once authorised, Stitch fires a <c>paymentInitiation.completed</c> webhook containing
///     a mandate token — <see cref="StitchPaymentProvider.ParseWebhookAsync"/> emits a typed
///     <see cref="Core.Models.Webhooks.MandateActivatedEvent"/> at that point.
///   </description></item>
///   <item><description>
///     <see cref="ChargeMandateAsync"/> issues <c>paymentInitiationDebit</c> against the
///     mandate token to pull funds. <see cref="CancelMandateAsync"/> issues
///     <c>cancelPaymentInitiation</c>.
///   </description></item>
/// </list>
/// <para>
/// Authentication uses OAuth2 client credentials against <see cref="StitchOptions.TokenEndpoint"/>
/// (default <c>https://secure.stitch.money/connect/token</c>). Access tokens are cached and
/// refreshed 60 seconds before expiry under a <see cref="SemaphoreSlim"/> to avoid thundering-herd
/// re-issue.
/// </para>
/// </remarks>
public sealed class StitchMandateProvider : BhenguProviderBase, IMandateProvider
{
    private const string DefaultTokenEndpoint = "https://secure.stitch.money/connect/token";
    private const string DefaultGraphqlEndpoint = "https://api.stitch.money/graphql";

    private readonly HttpClient _httpClient;
    private readonly StitchOptions _options;
    private readonly Uri _graphqlUri;
    private readonly Uri _tokenUri;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.Stitch;

    /// <summary>
    /// Construct the provider. Throws <see cref="ProviderConfigurationException"/> when
    /// <see cref="StitchOptions.ClientId"/> or <see cref="StitchOptions.ClientSecret"/> is unset
    /// (DebiCheck operations require the full OAuth2 client_credentials flow, not the simpler
    /// API-key shortcut that the payment provider supports).
    /// </summary>
    public StitchMandateProvider(
        HttpClient httpClient,
        IOptions<StitchOptions> options,
        ILogger<StitchMandateProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(StitchOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName,
                $"{nameof(StitchOptions.ClientSecret)} is required for DebiCheck mandate operations");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-staging.stitch.money"
                : _options.BaseUrl ?? "https://api.stitch.money";
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
        }

        _graphqlUri = Uri.TryCreate(_options.GraphqlEndpoint, UriKind.Absolute, out var gAbs)
            ? gAbs
            : (Uri.TryCreate(DefaultGraphqlEndpoint, UriKind.Absolute, out var gDef)
                ? gDef
                : new Uri(_httpClient.BaseAddress, "graphql"));

        _tokenUri = Uri.TryCreate(_options.TokenEndpoint, UriKind.Absolute, out var tAbs)
            ? tAbs
            : new Uri(DefaultTokenEndpoint, UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amount = request.AmountLimit.ToString("F2", CultureInfo.InvariantCulture);
        var currency = request.Currency.ToUpperInvariant();
        var externalReference = request.IdempotencyKey ?? $"mandate-{Guid.NewGuid():N}";

        var query = """
            mutation CreateDebiCheckMandate(
              $amount: MoneyInput!, $description: String!, $externalReference: String!,
              $payerReference: String!, $beneficiaryReference: String!
            ) {
              createPaymentInitiation(input: {
                amount: $amount,
                payerReference: $payerReference,
                beneficiaryReference: $beneficiaryReference,
                externalReference: $externalReference,
                debiCheck: { description: $description }
              }) {
                paymentInitiation { id authorizationUrl status }
              }
            }
            """;

        var variables = new
        {
            amount = new { quantity = amount, currency },
            description = request.Description,
            externalReference,
            payerReference = request.CustomerId,
            beneficiaryReference = request.CustomerId
        };

        var body = await SendGraphqlAsync(query, variables, ct, "CreateMandate").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<StitchGraphqlResponse<StitchCreateMandateData>>(body);
        var pi = parsed?.Data?.CreatePaymentInitiation?.PaymentInitiation;

        Logger.LogInformation("Stitch DebiCheck mandate created: id={Id} authUrl={Url}",
            pi?.Id, pi?.AuthorizationUrl);

        return new Mandate
        {
            Reference = pi?.Id ?? externalReference,
            CustomerId = request.CustomerId,
            Status = MapStatus(pi?.Status),
            AmountLimit = request.AmountLimit,
            Currency = currency,
            AuthorisedAt = null,
            CancelledAt = null,
            AuthorisationUrl = pi?.AuthorizationUrl
        };
    }

    /// <inheritdoc />
    public async Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mandateReference);

        var query = """
            query GetPaymentInitiation($id: ID!) {
              node(id: $id) {
                ... on PaymentInitiation {
                  id
                  status
                  authorizationUrl
                  amount { quantity currency }
                  payer { reference }
                }
              }
            }
            """;

        try
        {
            var body = await SendGraphqlAsync(query, new { id = mandateReference }, ct, "GetMandate").ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<StitchGraphqlResponse<StitchGetMandateData>>(body);
            var node = parsed?.Data?.Node;
            if (node is null || string.IsNullOrEmpty(node.Id)) return null;

            var status = MapStatus(node.Status);
            return new Mandate
            {
                Reference = node.Id,
                CustomerId = node.Payer?.Reference ?? string.Empty,
                Status = status,
                AmountLimit = decimal.TryParse(node.Amount?.Quantity, NumberStyles.Number, CultureInfo.InvariantCulture, out var q) ? q : 0m,
                Currency = node.Amount?.Currency ?? "ZAR",
                AuthorisedAt = status == MandateStatus.Active ? DateTime.UtcNow : null,
                CancelledAt = status == MandateStatus.Cancelled ? DateTime.UtcNow : null,
                AuthorisationUrl = node.AuthorizationUrl
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mandateReference);

        var query = """
            mutation CancelPaymentInitiation($id: ID!) {
              cancelPaymentInitiation(input: { paymentInitiationId: $id }) {
                paymentInitiation { id status }
              }
            }
            """;

        try
        {
            var body = await SendGraphqlAsync(query, new { id = mandateReference }, ct, "CancelMandate").ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<StitchGraphqlResponse<StitchCancelMandateData>>(body);
            var pi = parsed?.Data?.CancelPaymentInitiation?.PaymentInitiation;

            Logger.LogInformation("Stitch DebiCheck mandate cancelled: id={Id} status={Status}",
                pi?.Id, pi?.Status);

            return new Mandate
            {
                Reference = pi?.Id ?? mandateReference,
                CustomerId = string.Empty,
                Status = MandateStatus.Cancelled,
                AmountLimit = 0m,
                Currency = "ZAR",
                CancelledAt = DateTime.UtcNow
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" || ex.ProviderErrorCode == "400" ||
                                                  (ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            // Idempotency contract: cancelling an already-cancelled / missing mandate returns success.
            Logger.LogInformation("Stitch CancelMandate treated as idempotent for {Reference}: {Code}",
                mandateReference, ex.ProviderErrorCode);
            return new Mandate
            {
                Reference = mandateReference,
                CustomerId = string.Empty,
                Status = MandateStatus.Cancelled,
                AmountLimit = 0m,
                Currency = "ZAR",
                CancelledAt = DateTime.UtcNow
            };
        }
    }

    /// <inheritdoc />
    public async Task<PaymentResponse> ChargeMandateAsync(MandateChargeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture);
        var currency = request.Currency.ToUpperInvariant();
        var externalReference = request.IdempotencyKey ?? $"debit-{Guid.NewGuid():N}";

        var query = """
            mutation DebitMandate(
              $amount: MoneyInput!, $paymentInitiationId: ID!,
              $description: String!, $externalReference: String!
            ) {
              paymentInitiationDebit(input: {
                paymentInitiationId: $paymentInitiationId,
                amount: $amount,
                description: $description,
                externalReference: $externalReference
              }) {
                debit { id status }
              }
            }
            """;

        var variables = new
        {
            amount = new { quantity = amount, currency },
            paymentInitiationId = request.MandateReference,
            description = request.Description,
            externalReference
        };

        var body = await SendGraphqlAsync(query, variables, ct, "ChargeMandate").ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize<StitchGraphqlResponse<StitchDebitMandateData>>(body);
        var debit = parsed?.Data?.PaymentInitiationDebit?.Debit;

        Logger.LogInformation("Stitch DebiCheck debit: id={Id} status={Status} mandate={Mandate}",
            debit?.Id, debit?.Status, request.MandateReference);

        return new PaymentResponse
        {
            GatewayReference = debit?.Id ?? externalReference,
            Status = MapPaymentStatus(debit?.Status),
            Amount = request.Amount,
            Currency = currency,
            ProcessedAt = DateTime.UtcNow,
            Message = debit?.Status
        };
    }

    private async Task<string> SendGraphqlAsync(string query, object variables, CancellationToken ct, string operation)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);

        var payload = new { query, variables };
        var json = JsonSerializer.Serialize(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, _graphqlUri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(_cachedToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);
        req.Headers.TryAddWithoutValidation("X-Stitch-Client-Id", _options.ClientId);

        return await ExecuteAsync(req, ct, operation).ConfigureAwait(false);
    }

    private async Task EnsureTokenAsync(CancellationToken ct)
    {
        if (_cachedToken is not null && _cachedTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
            return;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedToken is not null && _cachedTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(60))
                return;

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _options.ClientId,
                ["client_secret"] = _options.ClientSecret ?? string.Empty,
                ["scope"] = "client_paymentrequest client_paymentauthorizationrequest"
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, _tokenUri) { Content = form };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, $"HTTP request to Stitch token endpoint failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderUnavailableException(ProviderName,
                    $"Stitch token endpoint returned {(int)response.StatusCode}: {body}");

            var token = JsonSerializer.Deserialize<StitchTokenResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "Stitch token endpoint returned no access_token");

            _cachedToken = token.AccessToken;
            _cachedTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
            Logger.LogDebug("Stitch access token cached, expires={Expires}", _cachedTokenExpiresAt);
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<string> ExecuteAsync(HttpRequestMessage req, CancellationToken ct, string operation)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Stitch failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Stitch {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private static MandateStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" or "authorized" or "authorised" or "completed" or "success" or "successful" => MandateStatus.Active,
        "pending" or "submitted" or "processing" or "awaitingauthorization" or "awaitingauthorisation" => MandateStatus.Pending,
        "paused" or "suspended" => MandateStatus.Paused,
        "cancelled" or "canceled" => MandateStatus.Cancelled,
        "expired" => MandateStatus.Expired,
        "rejected" or "declined" or "failed" => MandateStatus.Rejected,
        _ => MandateStatus.Pending
    };

    private static PaymentStatus MapPaymentStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "settled" => PaymentStatus.Completed,
        "pending" or "processing" or "submitted" => PaymentStatus.Pending,
        "failed" or "rejected" or "declined" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    // === Stitch API response shapes (internal) ===

    private sealed class StitchTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } = 3600;
    }

    private sealed class StitchGraphqlResponse<T>
    {
        [JsonPropertyName("data")] public T? Data { get; set; }
    }

    private sealed class StitchCreateMandateData
    {
        [JsonPropertyName("createPaymentInitiation")] public StitchCreateMandate? CreatePaymentInitiation { get; set; }
    }

    private sealed class StitchCreateMandate
    {
        [JsonPropertyName("paymentInitiation")] public StitchPaymentInitiation? PaymentInitiation { get; set; }
    }

    private sealed class StitchPaymentInitiation
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("authorizationUrl")] public string? AuthorizationUrl { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class StitchGetMandateData
    {
        [JsonPropertyName("node")] public StitchMandateNode? Node { get; set; }
    }

    private sealed class StitchMandateNode
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("authorizationUrl")] public string? AuthorizationUrl { get; set; }
        [JsonPropertyName("amount")] public StitchMoney? Amount { get; set; }
        [JsonPropertyName("payer")] public StitchPayer? Payer { get; set; }
    }

    private sealed class StitchMoney
    {
        [JsonPropertyName("quantity")] public string? Quantity { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class StitchPayer
    {
        [JsonPropertyName("reference")] public string? Reference { get; set; }
    }

    private sealed class StitchCancelMandateData
    {
        [JsonPropertyName("cancelPaymentInitiation")] public StitchCancelMandate? CancelPaymentInitiation { get; set; }
    }

    private sealed class StitchCancelMandate
    {
        [JsonPropertyName("paymentInitiation")] public StitchPaymentInitiation? PaymentInitiation { get; set; }
    }

    private sealed class StitchDebitMandateData
    {
        [JsonPropertyName("paymentInitiationDebit")] public StitchDebitMandate? PaymentInitiationDebit { get; set; }
    }

    private sealed class StitchDebitMandate
    {
        [JsonPropertyName("debit")] public StitchDebit? Debit { get; set; }
    }

    private sealed class StitchDebit
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
