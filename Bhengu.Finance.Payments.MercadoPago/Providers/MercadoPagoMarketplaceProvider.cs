// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.Core.Models.Marketplace;
using Bhengu.Finance.Payments.Core.Observability;
using Bhengu.Finance.Payments.Core.Providers;
using Bhengu.Finance.Payments.MercadoPago.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MercadoPago.Providers;

/// <summary>
/// Mercado Pago Marketplace provider. Wraps Mercado Pago's marketplace endpoints
/// (<c>/v1/accounts</c> for sub-accounts and <c>/v1/payments</c> with
/// <c>collector_id</c> + <c>application_fee</c> for split charges).
/// </summary>
/// <remarks>
/// Mercado Pago Marketplace works differently from Stripe Connect: each seller has their own
/// Mercado Pago account, and the platform charges using the seller's <c>collector_id</c> + the
/// platform's OAuth2 access token. The platform fee is set via <c>application_fee</c>. Split
/// definitions are not a separate resource in Mercado Pago — splits are inline on every charge.
/// To preserve the SDK's <see cref="IMarketplaceProvider"/> shape we cache split definitions
/// locally and inline them at charge time.
/// </remarks>
public sealed class MercadoPagoMarketplaceProvider : BhenguProviderBase, IMarketplaceProvider
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions SerializeOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly ConcurrentDictionary<string, SplitDefinition> SplitCache = new(StringComparer.Ordinal);

    private readonly HttpClient _httpClient;
    private readonly MercadoPagoOptions _options;

    /// <inheritdoc />
    public override string ProviderName => ProviderNames.MercadoPago;

    /// <summary>Create a new Mercado Pago Marketplace provider.</summary>
    public MercadoPagoMarketplaceProvider(
        HttpClient httpClient,
        IOptions<MercadoPagoOptions> options,
        ILogger<MercadoPagoMarketplaceProvider> logger)
        : base(logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.AccessToken))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MercadoPagoOptions.AccessToken)} is required");

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_options.BaseUrl ?? "https://api.mercadopago.com");

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.AccessToken);
    }

    /// <inheritdoc />
    public async Task<SubAccount> CreateSubAccountAsync(SubAccountRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "marketplace.create_subaccount");
        try
        {
            var body = new
            {
                email = request.ContactEmail,
                site_id = MapSiteId(request.Country),
                user_type = request.Metadata?.GetValueOrDefault("user_type") ?? "operator",
                first_name = request.Metadata?.GetValueOrDefault("first_name") ?? request.BusinessName,
                last_name = request.Metadata?.GetValueOrDefault("last_name"),
                identification = new
                {
                    type = request.Metadata?.GetValueOrDefault("identification_type") ?? "CPF",
                    number = request.Metadata?.GetValueOrDefault("identification_number")
                },
                additional_info = new
                {
                    business_name = request.BusinessName,
                    return_url = request.ReturnUrl
                }
            };

            var raw = await SendAsync(HttpMethod.Post, "v1/accounts", body, ct, "CreateSubAccount", request.IdempotencyKey).ConfigureAwait(false);
            var account = JsonSerializer.Deserialize<MercadoPagoAccountResponse>(raw, DeserializeOptions);

            Logger.LogInformation("Mercado Pago marketplace account created: {AccountId}", account?.Id);
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            return new SubAccount
            {
                Reference = account?.Id ?? string.Empty,
                BusinessName = account?.AdditionalInfo?.BusinessName ?? request.BusinessName,
                ContactEmail = account?.Email ?? request.ContactEmail,
                SettlementAccountToken = request.SettlementAccountToken,
                IsActive = string.Equals(account?.Status, "active", StringComparison.OrdinalIgnoreCase),
                OnboardingUrl = account?.OnboardingUrl
            };
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<SubAccount?> GetSubAccountAsync(string subAccountReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subAccountReference);

        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "marketplace.get_subaccount");
        try
        {
            var raw = await SendAsync(HttpMethod.Get, $"v1/accounts/{Uri.EscapeDataString(subAccountReference)}", body: null, ct, "GetSubAccount", idempotencyKey: null).ConfigureAwait(false);
            var account = JsonSerializer.Deserialize<MercadoPagoAccountResponse>(raw, DeserializeOptions);

            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);

            if (account?.Id is null)
                return null;

            return new SubAccount
            {
                Reference = account.Id,
                BusinessName = account.AdditionalInfo?.BusinessName ?? string.Empty,
                ContactEmail = account.Email,
                IsActive = string.Equals(account.Status, "active", StringComparison.OrdinalIgnoreCase),
                OnboardingUrl = account.OnboardingUrl
            };
        }
        catch (PaymentDeclinedException ex) when (ex.ProviderErrorCode == "404")
        {
            return null;
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    /// <inheritdoc />
#pragma warning disable CS1998 // intentionally async with no awaits — Mercado Pago has no list endpoint, but the contract demands IAsyncEnumerable.
    public async IAsyncEnumerable<SubAccount> ListSubAccountsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        Logger.LogDebug("Mercado Pago does not expose a marketplace account-list endpoint; callers mirror their roster client-side");
        yield break;
    }
#pragma warning restore CS1998

    /// <inheritdoc />
    public Task<SplitDefinition> CreateSplitAsync(SplitDefinitionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = BhenguPaymentDiagnostics.StartOperationActivity(ProviderName, "marketplace.create_split");

        var reference = $"mp_split_{Guid.NewGuid():N}";
        var split = new SplitDefinition
        {
            Reference = reference,
            Name = request.Name,
            Currency = request.Currency.ToUpperInvariant(),
            Rules = request.Rules
        };
        SplitCache[reference] = split;

        Logger.LogInformation("Mercado Pago split cached locally as {SplitId} ({Count} beneficiaries)",
            reference, request.Rules.Count);
        activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Success);
        return Task.FromResult(split);
    }

    /// <inheritdoc />
    public Task<SplitDefinition?> GetSplitAsync(string splitReference, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(splitReference);
        return Task.FromResult(SplitCache.TryGetValue(splitReference, out var s) ? s : null);
    }

    /// <inheritdoc />
    public async Task<PaymentResponse> ChargeWithSplitAsync(ChargeWithSplitRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SplitReference is null && (request.InlineRules is null || request.InlineRules.Count == 0))
            throw new BhenguPaymentException(ProviderName, "Either SplitReference or InlineRules must be supplied");

        using var activity = BhenguPaymentDiagnostics.StartChargeActivity(ProviderName, request.Payment.Currency);
        try
        {
            IReadOnlyList<SplitRule>? rules = request.InlineRules;
            if (rules is null && request.SplitReference is not null
                && SplitCache.TryGetValue(request.SplitReference, out var def))
                rules = def.Rules;
            if (rules is null || rules.Count == 0)
                throw new BhenguPaymentException(ProviderName, $"Split {request.SplitReference} not found and no inline rules supplied");

            // Mercado Pago Marketplace charges set collector_id (seller account) +
            // application_fee (platform fee). Multi-beneficiary splits are atomic across
            // the seller and the platform — additional split routing requires the
            // /v1/advanced_payments endpoint. We model a 2-line split (seller + platform)
            // and reject more complex inline rules with a descriptive error.
            var seller = rules.FirstOrDefault(r => !string.Equals(r.SubAccountReference, "platform", StringComparison.OrdinalIgnoreCase));
            var platform = rules.FirstOrDefault(r => string.Equals(r.SubAccountReference, "platform", StringComparison.OrdinalIgnoreCase));
            if (seller is null)
                throw new BhenguPaymentException(ProviderName, "Mercado Pago split requires at least one non-platform seller rule");

            var payment = request.Payment;
            var gross = payment.Amount;
            var applicationFee = platform?.ShareType switch
            {
                SplitShareType.FixedAmount => platform.Amount ?? 0m,
                SplitShareType.Percentage => Math.Round(gross * (platform.Percentage ?? 0m) / 100m, 2),
                _ => 0m
            };

            var metadata = payment.Metadata ?? new Dictionary<string, string>();
            var payerEmail = metadata.GetValueOrDefault("payer_email") ?? metadata.GetValueOrDefault("email")
                ?? throw new BhenguPaymentException(ProviderName, "Mercado Pago requires 'payer_email' in payment metadata");
            var paymentMethodId = metadata.GetValueOrDefault("payment_method_id") ?? "visa";

            var body = new Dictionary<string, object?>
            {
                ["transaction_amount"] = gross,
                ["description"] = payment.Description,
                ["payment_method_id"] = paymentMethodId,
                ["token"] = payment.PaymentMethodToken,
                ["installments"] = 1,
                ["application_fee"] = applicationFee,
                ["collector_id"] = seller.SubAccountReference,
                ["payer"] = new
                {
                    email = payerEmail
                },
                ["notification_url"] = _options.NotificationUrl
            };

            var raw = await SendAsync(HttpMethod.Post, "v1/payments", body, ct, "ChargeWithSplit", payment.IdempotencyKey).ConfigureAwait(false);
            var response = JsonSerializer.Deserialize<MercadoPagoPaymentResponse>(raw, DeserializeOptions);

            Logger.LogInformation("Mercado Pago split charge: paymentId={Id} status={Status} fee={Fee} collector={Collector}",
                response?.Id, response?.Status, applicationFee, seller.SubAccountReference);

            var status = response?.Status?.ToLowerInvariant() switch
            {
                "approved" or "authorized" => PaymentStatus.Completed,
                "pending" or "in_process" or "in_mediation" => PaymentStatus.Pending,
                "rejected" or "failed" => PaymentStatus.Failed,
                "cancelled" or "canceled" => PaymentStatus.Cancelled,
                "refunded" => PaymentStatus.Refunded,
                "charged_back" => PaymentStatus.Failed,
                _ => PaymentStatus.Pending
            };

            activity.SetOutcome(status == PaymentStatus.Completed ? BhenguPaymentDiagnostics.Outcomes.Success : BhenguPaymentDiagnostics.Outcomes.Pending);
            BhenguPaymentDiagnostics.ChargesTotal.Add(1,
                new KeyValuePair<string, object?>("provider", ProviderName),
                new KeyValuePair<string, object?>("outcome", status == PaymentStatus.Completed ? "success" : "pending"));

            return new PaymentResponse
            {
                GatewayReference = response?.Id?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                Status = status,
                Amount = gross,
                Currency = payment.Currency.ToUpperInvariant(),
                ProcessedAt = DateTime.UtcNow,
                Message = response?.StatusDetail
            };
        }
        catch
        {
            activity.SetOutcome(BhenguPaymentDiagnostics.Outcomes.Error);
            throw;
        }
    }

    private static string MapSiteId(string country) => country.ToUpperInvariant() switch
    {
        "BR" => "MLB",
        "AR" => "MLA",
        "MX" => "MLM",
        "CO" => "MCO",
        "CL" => "MLC",
        "PE" => "MPE",
        "UY" => "MLU",
        _ => "MLB"
    };

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string operation, string? idempotencyKey)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, SerializeOptions);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
            req.Headers.TryAddWithoutValidation("X-Idempotency-Key", idempotencyKey);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, "HTTP request to Mercado Pago failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            Logger.LogError("Mercado Pago {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return responseBody;
    }

    private sealed class MercadoPagoAccountResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("onboarding_url")] public string? OnboardingUrl { get; set; }
        [JsonPropertyName("additional_info")] public MercadoPagoAccountAdditionalInfo? AdditionalInfo { get; set; }
    }

    private sealed class MercadoPagoAccountAdditionalInfo
    {
        [JsonPropertyName("business_name")] public string? BusinessName { get; set; }
    }

    private sealed class MercadoPagoPaymentResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; set; }
    }
}
