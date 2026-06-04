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
using Bhengu.Finance.Payments.TymeBank.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mandate = Bhengu.Finance.Payments.Core.Models.Mandate.Mandate;

namespace Bhengu.Finance.Payments.TymeBank.Providers;

/// <summary>
/// TymeBank implementation of <see cref="IMandateProvider"/> — wraps the TymeBank debit-order
/// mandate API (<c>/v1/mandates</c>) under the unified Bhengu mandate contract.
/// </summary>
/// <remarks>
/// <para>
/// TymeBank's mandate flow is two-stage:
/// </para>
/// <list type="number">
///   <item><description>
///     <see cref="CreateMandateAsync"/> issues <c>POST /v1/mandates</c> with the payer's bank
///     account details (parsed from <see cref="MandateRequest.BankAccountToken"/>, formatted as
///     <c>"accountNumber:branchCode:accountHolderName"</c> or as a JSON document containing the
///     same three fields). TymeBank validates with the payer via their TymeBank mobile app and
///     returns the mandate id plus initial status (typically <c>pending</c>).
///   </description></item>
///   <item><description>
///     The payer authorises in-app; TymeBank then fires a <c>mandate.activated</c> webhook —
///     <see cref="TymeBankPaymentProvider.ParseWebhookAsync"/> emits a typed
///     <see cref="Core.Models.Webhooks.MandateActivatedEvent"/>.
///   </description></item>
///   <item><description>
///     <see cref="ChargeMandateAsync"/> issues <c>POST /v1/mandates/{id}/debit</c> to pull
///     funds. <see cref="CancelMandateAsync"/> issues <c>DELETE /v1/mandates/{id}</c>.
///   </description></item>
/// </list>
/// <para>
/// Authentication reuses the same OAuth2 client_credentials access-token cache as the
/// <see cref="TymeBankPaymentProvider"/> (cached for the lifetime of the HttpClient, refreshed
/// 60 seconds before expiry under a <see cref="SemaphoreSlim"/>).
/// </para>
/// </remarks>
public sealed class TymeBankMandateProvider : BhenguProviderBase, IMandateProvider
{
    private readonly HttpClient _httpClient;
    private readonly TymeBankOptions _options;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.TymeBank;

    /// <summary>
    /// Construct the provider. Throws <see cref="ProviderConfigurationException"/> when
    /// <see cref="TymeBankOptions.ClientId"/> or <see cref="TymeBankOptions.ClientSecret"/> is unset.
    /// </summary>
    public TymeBankMandateProvider(
        HttpClient httpClient,
        IOptions<TymeBankOptions> options,
        ILogger<TymeBankMandateProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ClientId))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(TymeBankOptions.ClientId)} is required");
        if (string.IsNullOrWhiteSpace(_options.ClientSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(TymeBankOptions.ClientSecret)} is required");

        if (_httpClient.BaseAddress is null)
        {
            var resolved = _options.UseSandbox
                ? _options.SandboxUrl ?? "https://api-sandbox.tymebank.co.za"
                : _options.BaseUrl ?? "https://api.tymebank.co.za";
            if (!resolved.EndsWith('/')) resolved += "/";
            _httpClient.BaseAddress = new Uri(resolved);
        }
    }

    /// <inheritdoc />
    public async Task<Mandate> CreateMandateAsync(MandateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (accountNumber, branchCode, accountHolder) = ParseBankAccountToken(request.BankAccountToken);

        var body = new
        {
            customer_reference = request.CustomerId,
            account_number = accountNumber,
            branch_code = branchCode,
            account_holder = accountHolder,
            amount_limit = request.AmountLimit.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            frequency = "adhoc",
            description = request.Description,
            start_date = request.StartAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            end_date = request.EndAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            callback_url = string.IsNullOrEmpty(_options.CallbackUrl) ? null : _options.CallbackUrl
        };

        var raw = await SendAsync(HttpMethod.Post, "v1/mandates", body, ct, "CreateMandate", request.IdempotencyKey)
            .ConfigureAwait(false);
        var mandateResp = JsonSerializer.Deserialize<TymeBankMandateResponse>(raw);

        Logger.LogInformation("TymeBank mandate created: id={MandateId} status={Status}",
            mandateResp?.MandateId, mandateResp?.Status);

        return MapMandate(mandateResp, fallbackCustomerId: request.CustomerId, fallbackCurrency: request.Currency.ToUpperInvariant(), fallbackAmountLimit: request.AmountLimit);
    }

    /// <inheritdoc />
    public async Task<Mandate?> GetMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mandateReference);

        try
        {
            var raw = await SendAsync(HttpMethod.Get, $"v1/mandates/{Uri.EscapeDataString(mandateReference)}", body: null, ct, "GetMandate")
                .ConfigureAwait(false);
            var mandateResp = JsonSerializer.Deserialize<TymeBankMandateResponse>(raw);
            if (mandateResp is null || string.IsNullOrEmpty(mandateResp.MandateId)) return null;
            return MapMandate(mandateResp);
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<Mandate> CancelMandateAsync(string mandateReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mandateReference);

        try
        {
            var raw = await SendAsync(HttpMethod.Delete, $"v1/mandates/{Uri.EscapeDataString(mandateReference)}", body: null, ct, "CancelMandate")
                .ConfigureAwait(false);

            TymeBankMandateResponse? mandateResp = null;
            if (!string.IsNullOrWhiteSpace(raw) && raw.TrimStart().StartsWith('{'))
                mandateResp = JsonSerializer.Deserialize<TymeBankMandateResponse>(raw);

            Logger.LogInformation("TymeBank mandate cancelled: id={MandateId}", mandateReference);

            return new Mandate
            {
                Reference = mandateResp?.MandateId ?? mandateReference,
                CustomerId = mandateResp?.CustomerReference ?? string.Empty,
                Status = MandateStatus.Cancelled,
                AmountLimit = decimal.TryParse(mandateResp?.AmountLimit, NumberStyles.Number, CultureInfo.InvariantCulture, out var l) ? l : 0m,
                Currency = mandateResp?.Currency ?? "ZAR",
                AuthorisedAt = mandateResp?.AuthorisedAt,
                CancelledAt = mandateResp?.CancelledAt ?? DateTime.UtcNow
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404" ||
                                                  (ex.ProviderErrorMessage?.Contains("already", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            // Idempotency contract: cancelling an already-cancelled / missing mandate returns success.
            Logger.LogInformation("TymeBank CancelMandate treated as idempotent for {Reference}: {Code}",
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

        var body = new
        {
            amount = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            currency = request.Currency.ToUpperInvariant(),
            narrative = request.Description,
            reference = request.IdempotencyKey ?? $"debit-{Guid.NewGuid():N}"
        };

        var raw = await SendAsync(HttpMethod.Post,
                $"v1/mandates/{Uri.EscapeDataString(request.MandateReference)}/debit",
                body, ct, "ChargeMandate", request.IdempotencyKey)
            .ConfigureAwait(false);
        var debitResp = JsonSerializer.Deserialize<TymeBankDebitResponse>(raw);

        Logger.LogInformation("TymeBank mandate debit: id={DebitId} status={Status} mandate={Mandate}",
            debitResp?.DebitId, debitResp?.Status, request.MandateReference);

        return new PaymentResponse
        {
            GatewayReference = debitResp?.DebitId ?? string.Empty,
            Status = MapPaymentStatus(debitResp?.Status),
            Amount = request.Amount,
            Currency = request.Currency.ToUpperInvariant(),
            ProcessedAt = DateTime.UtcNow,
            Message = debitResp?.Status
        };
    }

    /// <summary>
    /// Parse the <see cref="MandateRequest.BankAccountToken"/> into account number, branch code, and account holder.
    /// Accepts two forms — colon-delimited <c>"accountNumber:branchCode:accountHolder"</c> (the wallet's preferred
    /// concise form) and a JSON object with <c>accountNumber</c>, <c>branchCode</c>, <c>accountHolder</c> keys.
    /// </summary>
    private static (string AccountNumber, string BranchCode, string AccountHolder) ParseBankAccountToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return (string.Empty, string.Empty, string.Empty);

        var trimmed = token.Trim();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<TymeBankAccountToken>(trimmed);
                if (parsed is not null)
                    return (parsed.AccountNumber ?? string.Empty, parsed.BranchCode ?? string.Empty, parsed.AccountHolder ?? string.Empty);
            }
            catch (JsonException) { /* fall through to colon parse */ }
        }

        var parts = trimmed.Split(':', 3, StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            >= 3 => (parts[0], parts[1], parts[2]),
            2 => (parts[0], parts[1], string.Empty),
            1 => (parts[0], string.Empty, string.Empty),
            _ => (string.Empty, string.Empty, string.Empty)
        };
    }

    private static Mandate MapMandate(TymeBankMandateResponse? m, string? fallbackCustomerId = null, string? fallbackCurrency = null, decimal? fallbackAmountLimit = null)
    {
        if (m is null)
        {
            return new Mandate
            {
                Reference = string.Empty,
                CustomerId = fallbackCustomerId ?? string.Empty,
                Status = MandateStatus.Pending,
                AmountLimit = fallbackAmountLimit ?? 0m,
                Currency = fallbackCurrency ?? "ZAR"
            };
        }

        return new Mandate
        {
            Reference = m.MandateId ?? string.Empty,
            CustomerId = m.CustomerReference ?? fallbackCustomerId ?? string.Empty,
            Status = MapStatus(m.Status),
            AmountLimit = decimal.TryParse(m.AmountLimit, NumberStyles.Number, CultureInfo.InvariantCulture, out var l)
                ? l
                : fallbackAmountLimit ?? 0m,
            Currency = m.Currency ?? fallbackCurrency ?? "ZAR",
            AuthorisedAt = m.AuthorisedAt,
            CancelledAt = m.CancelledAt,
            AuthorisationUrl = m.AuthorisationUrl
        };
    }

    private static MandateStatus MapStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "active" or "authorized" or "authorised" => MandateStatus.Active,
        "pending" or "awaiting_authorisation" or "awaiting_authorization" or "processing" => MandateStatus.Pending,
        "paused" or "suspended" => MandateStatus.Paused,
        "cancelled" or "canceled" => MandateStatus.Cancelled,
        "expired" => MandateStatus.Expired,
        "rejected" or "declined" or "failed" => MandateStatus.Rejected,
        _ => MandateStatus.Pending
    };

    private static PaymentStatus MapPaymentStatus(string? raw) => raw?.ToLowerInvariant() switch
    {
        "success" or "successful" or "completed" or "settled" or "captured" => PaymentStatus.Completed,
        "pending" or "processing" or "queued" or "submitted" => PaymentStatus.Pending,
        "failed" or "rejected" or "declined" or "insufficient_funds" => PaymentStatus.Failed,
        "cancelled" or "canceled" => PaymentStatus.Cancelled,
        "refunded" => PaymentStatus.Refunded,
        _ => PaymentStatus.Pending
    };

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation, string? idempotencyKey = null)
    {
        await EnsureTokenAsync(ct).ConfigureAwait(false);

        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonWriteOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrEmpty(_cachedToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cachedToken);
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to TymeBank failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("TymeBank {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
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
                ["client_secret"] = _options.ClientSecret
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, "oauth2/token") { Content = form };

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "HTTP request to TymeBank oauth2/token failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ProviderUnavailableException(ProviderName,
                    $"TymeBank oauth2/token returned {(int)response.StatusCode}: {body}");

            var token = JsonSerializer.Deserialize<TymeBankTokenResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "TymeBank oauth2/token returned no access_token");

            _cachedToken = token.AccessToken;
            _cachedTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, token.ExpiresIn - 60));
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // === TymeBank API response shapes (internal) ===

    private sealed class TymeBankTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; } = 3600;
    }

    private sealed class TymeBankMandateResponse
    {
        [JsonPropertyName("mandate_id")] public string? MandateId { get; set; }
        [JsonPropertyName("customer_reference")] public string? CustomerReference { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("amount_limit")] public string? AmountLimit { get; set; }
        [JsonPropertyName("currency")] public string? Currency { get; set; }
        [JsonPropertyName("authorised_at")] public DateTime? AuthorisedAt { get; set; }
        [JsonPropertyName("cancelled_at")] public DateTime? CancelledAt { get; set; }
        [JsonPropertyName("authorisation_url")] public string? AuthorisationUrl { get; set; }
    }

    private sealed class TymeBankDebitResponse
    {
        [JsonPropertyName("debit_id")] public string? DebitId { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class TymeBankAccountToken
    {
        [JsonPropertyName("accountNumber")] public string? AccountNumber { get; set; }
        [JsonPropertyName("branchCode")] public string? BranchCode { get; set; }
        [JsonPropertyName("accountHolder")] public string? AccountHolder { get; set; }
    }
}
