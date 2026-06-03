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
using Bhengu.Finance.Payments.MPesa.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.MPesa.Providers;

/// <summary>
/// Safaricom M-Pesa (Daraja) provider. Implements C2B via STK Push, B2C disbursements (<see cref="IPayoutProvider"/>),
/// and Transaction Reversal for refunds.
/// <para>
/// <b>Webhook signature note:</b> M-Pesa does NOT sign callback payloads. Verification relies on the
/// callback URL containing an unguessable token (<see cref="MPesaOptions.CallbackUrlToken"/>) which the caller
/// MUST verify out-of-band before passing the payload to <see cref="VerifyWebhookSignature"/>.
/// The <paramref name="signature"/> argument to <see cref="VerifyWebhookSignature"/> is treated as the URL-path token.
/// </para>
/// </summary>
public sealed class MPesaPaymentProvider : IPaymentGatewayProvider, IPayoutProvider
{
    private readonly HttpClient _httpClient;
    private readonly MPesaOptions _options;
    private readonly ILogger<MPesaPaymentProvider> _logger;
    private readonly string _baseUrl;

    private string? _cachedAccessToken;
    private DateTime _cachedTokenExpiresUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public string ProviderName => ProviderNames.MPesa;

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Charge |
        ProviderCapabilities.Refund |
        ProviderCapabilities.Payout |
        ProviderCapabilities.Webhook |
        ProviderCapabilities.MobileMoney;

    public MPesaPaymentProvider(
        HttpClient httpClient,
        IOptions<MPesaOptions> options,
        ILogger<MPesaPaymentProvider> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ConsumerKey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.ConsumerKey)} is required");
        if (string.IsNullOrWhiteSpace(_options.ConsumerSecret))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.ConsumerSecret)} is required");
        if (string.IsNullOrWhiteSpace(_options.BusinessShortCode))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.BusinessShortCode)} is required");
        if (string.IsNullOrWhiteSpace(_options.Passkey))
            throw new ProviderConfigurationException(ProviderName, $"{nameof(MPesaOptions.Passkey)} is required");

        _baseUrl = _options.UseSandbox
            ? (_options.SandboxUrl ?? "https://sandbox.safaricom.co.ke/")
            : (_options.BaseUrl ?? "https://api.safaricom.co.ke/");

        if (!_baseUrl.EndsWith('/')) _baseUrl += "/";

        if (_httpClient.BaseAddress is null)
            _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    public async Task<PaymentResponse> ProcessPaymentAsync(PaymentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var phoneNumber = request.PaymentMethodToken;
        if (string.IsNullOrWhiteSpace(phoneNumber))
            throw new PaymentDeclinedException(ProviderName, "missing_phone",
                "M-Pesa STK Push requires the payer MSISDN in PaymentRequest.PaymentMethodToken (e.g. 2547xxxxxxxx).");

        var amount = (int)Math.Round(request.Amount, MidpointRounding.AwayFromZero);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var password = BuildStkPassword(_options.BusinessShortCode, _options.Passkey, timestamp);
        var accountRef = request.Metadata?.TryGetValue("account_reference", out var ar) == true
            ? ar
            : request.Description.Length > 12 ? request.Description[..12] : request.Description;

        var body = new
        {
            BusinessShortCode = _options.BusinessShortCode,
            Password = password,
            Timestamp = timestamp,
            TransactionType = "CustomerPayBillOnline",
            Amount = amount,
            PartyA = phoneNumber,
            PartyB = _options.BusinessShortCode,
            PhoneNumber = phoneNumber,
            CallBackURL = _options.CallbackUrl,
            AccountReference = accountRef,
            TransactionDesc = request.Description
        };

        var (responseBody, _) = await SendAsync(
            HttpMethod.Post, "mpesa/stkpush/v1/processrequest", body, ct, "STKPush", requireAuth: true).ConfigureAwait(false);

        var stk = JsonSerializer.Deserialize<MPesaStkPushResponse>(responseBody);

        _logger.LogInformation(
            "M-Pesa STK Push initiated: MerchantRequestID={MerchantRequestId} CheckoutRequestID={CheckoutRequestId} ResponseCode={ResponseCode}",
            stk?.MerchantRequestID, stk?.CheckoutRequestID, stk?.ResponseCode);

        // ResponseCode "0" means request accepted for processing — settlement happens asynchronously via callback.
        var status = stk?.ResponseCode == "0" ? PaymentStatus.Pending : PaymentStatus.Failed;

        return new PaymentResponse
        {
            GatewayReference = stk?.CheckoutRequestID ?? string.Empty,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow,
            Message = stk?.CustomerMessage ?? stk?.ResponseDescription
        };
    }

    public async Task<RefundResponse> ProcessRefundAsync(RefundRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.InitiatorName) || string.IsNullOrWhiteSpace(_options.SecurityCredential))
            throw new ProviderConfigurationException(ProviderName,
                $"M-Pesa Transaction Reversal requires {nameof(MPesaOptions.InitiatorName)} and {nameof(MPesaOptions.SecurityCredential)}");

        var amount = (int)Math.Round(request.Amount, MidpointRounding.AwayFromZero);
        var body = new
        {
            Initiator = _options.InitiatorName,
            SecurityCredential = _options.SecurityCredential,
            CommandID = "TransactionReversal",
            TransactionID = request.GatewayReference,
            Amount = amount,
            ReceiverParty = _options.BusinessShortCode,
            RecieverIdentifierType = "11", // Daraja API uses the misspelled field name "Reciever".
            ResultURL = _options.ResultUrl,
            QueueTimeOutURL = _options.QueueTimeoutUrl,
            Remarks = request.Reason,
            Occasion = request.Reason
        };

        var (responseBody, _) = await SendAsync(
            HttpMethod.Post, "mpesa/reversal/v1/request", body, ct, "Reversal", requireAuth: true).ConfigureAwait(false);

        var reversal = JsonSerializer.Deserialize<MPesaReversalResponse>(responseBody);

        _logger.LogInformation(
            "M-Pesa Reversal accepted: ConversationID={ConversationId} OriginatorConversationID={OriginatorConversationId} ResponseCode={ResponseCode}",
            reversal?.ConversationID, reversal?.OriginatorConversationID, reversal?.ResponseCode);

        var status = reversal?.ResponseCode == "0" ? PaymentStatus.Pending : PaymentStatus.Failed;

        return new RefundResponse
        {
            GatewayReference = reversal?.ConversationID ?? string.Empty,
            Amount = request.Amount,
            Status = status,
            ProcessedAt = DateTime.UtcNow,
            Message = reversal?.ResponseDescription
        };
    }

    public async Task<PayoutResponse> ProcessPayoutAsync(PayoutRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(_options.InitiatorName) || string.IsNullOrWhiteSpace(_options.SecurityCredential))
            throw new ProviderConfigurationException(ProviderName,
                $"M-Pesa B2C requires {nameof(MPesaOptions.InitiatorName)} and {nameof(MPesaOptions.SecurityCredential)}");

        if (string.IsNullOrWhiteSpace(request.DestinationToken))
            throw new PaymentDeclinedException(ProviderName, "invalid_msisdn",
                "M-Pesa B2C requires the recipient MSISDN in PayoutRequest.DestinationToken (e.g. 254712345678).");

        var amount = (int)Math.Round(request.Amount, MidpointRounding.AwayFromZero);
        // OriginatorConversationID is M-Pesa's idempotency knob — caller-supplied value collapses retries server-side.
        var originatorConversationId = request.IdempotencyKey ?? Guid.NewGuid().ToString();
        var commandId = request.Metadata?.TryGetValue("command_id", out var cid) == true ? cid : "BusinessPayment";

        var body = new
        {
            OriginatorConversationID = originatorConversationId,
            InitiatorName = _options.InitiatorName,
            SecurityCredential = _options.SecurityCredential,
            CommandID = commandId,
            Amount = amount,
            PartyA = _options.BusinessShortCode,
            PartyB = request.DestinationToken,
            Remarks = request.Description,
            QueueTimeOutURL = _options.QueueTimeoutUrl,
            ResultURL = _options.ResultUrl,
            Occasion = request.Description
        };

        var (responseBody, _) = await SendAsync(
            HttpMethod.Post, "mpesa/b2c/v3/paymentrequest", body, ct, "B2C", requireAuth: true).ConfigureAwait(false);

        var b2c = JsonSerializer.Deserialize<MPesaB2CResponse>(responseBody);

        _logger.LogInformation(
            "M-Pesa B2C accepted: OriginatorConversationID={OriginatorConversationId} ConversationID={ConversationId} ResponseCode={ResponseCode}",
            b2c?.OriginatorConversationID, b2c?.ConversationID, b2c?.ResponseCode);

        var status = b2c?.ResponseCode == "0" ? PaymentStatus.Pending : PaymentStatus.Failed;

        return new PayoutResponse
        {
            GatewayReference = b2c?.ConversationID ?? originatorConversationId,
            Status = status,
            Amount = request.Amount,
            Currency = request.Currency,
            ProcessedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// M-Pesa does NOT sign webhook payloads. Verification is by callback-URL token comparison.
    /// Pass the token extracted from the URL path as <paramref name="signature"/>.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        ArgumentException.ThrowIfNullOrEmpty(signature);

        if (string.IsNullOrWhiteSpace(_options.CallbackUrlToken))
        {
            _logger.LogWarning("M-Pesa CallbackUrlToken not configured — webhook source cannot be authenticated.");
            return false;
        }

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(signature),
            Encoding.UTF8.GetBytes(_options.CallbackUrlToken));
    }

    public Task<WebhookEvent?> ParseWebhookAsync(string payload, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);

        try
        {
            var envelope = JsonSerializer.Deserialize<MPesaCallbackEnvelope>(payload);

            // STK Push (collection) callback — { "Body": { "stkCallback": { ... } } }
            var stk = envelope?.Body?.StkCallback;
            if (stk is not null && !string.IsNullOrEmpty(stk.CheckoutRequestID))
            {
                var status = stk.ResultCode == 0 ? PaymentStatus.Completed : PaymentStatus.Failed;

                _logger.LogInformation(
                    "Parsed M-Pesa STK callback: CheckoutRequestID={CheckoutRequestId} ResultCode={ResultCode}",
                    stk.CheckoutRequestID, stk.ResultCode);

                return Task.FromResult<WebhookEvent?>(new WebhookEvent
                {
                    GatewayReference = stk.CheckoutRequestID,
                    Status = status,
                    EventType = stk.ResultCode == 0 ? "stkcallback.success" : "stkcallback.failure",
                    Category = stk.ResultCode == 0
                        ? Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargeSucceeded
                        : Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.ChargeFailed
                });
            }

            // B2C / Reversal Result callback — { "Result": { "ResultCode": 0, "ConversationID": ..., ... } }
            var result = envelope?.Result;
            if (result is not null && !string.IsNullOrEmpty(result.ConversationID))
            {
                var succeeded = result.ResultCode == 0;

                _logger.LogInformation(
                    "Parsed M-Pesa Result callback: ConversationID={ConversationId} TransactionID={TransactionId} ResultCode={ResultCode}",
                    result.ConversationID, result.TransactionID, result.ResultCode);

                // Surface as a typed payout event so consumers can switch on the concrete record.
                if (succeeded)
                {
                    return Task.FromResult<WebhookEvent?>(new Bhengu.Finance.Payments.Core.Models.Webhooks.PayoutCompletedEvent
                    {
                        GatewayReference = result.TransactionID ?? result.ConversationID,
                        PayoutReference = result.TransactionID ?? result.ConversationID,
                        Status = PaymentStatus.Completed,
                        EventType = "b2c.result.success",
                        Category = Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.PayoutCompleted,
                        Amount = ExtractAmount(result),
                        Currency = "KES",
                        DestinationToken = ExtractRecipientMsisdn(result)
                    });
                }

                return Task.FromResult<WebhookEvent?>(new Bhengu.Finance.Payments.Core.Models.Webhooks.PayoutFailedEvent
                {
                    GatewayReference = result.TransactionID ?? result.ConversationID,
                    PayoutReference = result.TransactionID ?? result.ConversationID,
                    Status = PaymentStatus.Failed,
                    EventType = "b2c.result.failure",
                    Category = Bhengu.Finance.Payments.Core.Models.Webhooks.WebhookEventCategory.PayoutFailed,
                    Amount = ExtractAmount(result),
                    Currency = "KES",
                    FailureCode = result.ResultCode.ToString(CultureInfo.InvariantCulture),
                    FailureMessage = result.ResultDesc
                });
            }

            return Task.FromResult<WebhookEvent?>(null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse M-Pesa webhook payload");
            return Task.FromResult<WebhookEvent?>(null);
        }
    }

    private static decimal ExtractAmount(MPesaResultCallback result)
    {
        if (result.ResultParameters?.ResultParameter is null) return 0m;
        foreach (var p in result.ResultParameters.ResultParameter)
        {
            if (!string.Equals(p.Key, "TransactionAmount", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDecimal(out var d)) return d;
            if (p.Value.ValueKind == JsonValueKind.String
                && decimal.TryParse(p.Value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s)) return s;
        }
        return 0m;
    }

    private static string? ExtractRecipientMsisdn(MPesaResultCallback result)
    {
        if (result.ResultParameters?.ResultParameter is null) return null;
        foreach (var p in result.ResultParameters.ResultParameter)
        {
            if (!string.Equals(p.Key, "ReceiverPartyPublicName", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
        }
        return null;
    }

    // ===== HTTP plumbing =====

    private async Task<(string Body, HttpResponseMessage Response)> SendAsync(
        HttpMethod method, string path, object body, CancellationToken ct, string operation, bool requireAuth)
    {
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (requireAuth)
        {
            var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderUnavailableException(ProviderName, $"HTTP request to M-Pesa ({operation}) failed", ex);
        }

        var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds is double d ? (int)d : (int?)null;
            throw new ProviderRateLimitException(ProviderName, retryAfter, responseBody);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("M-Pesa {Operation} failed: {StatusCode} {Body}", operation, response.StatusCode, responseBody);
            if ((int)response.StatusCode is >= 400 and < 500)
                throw new PaymentDeclinedException(ProviderName, ((int)response.StatusCode).ToString(), responseBody);
            throw new ProviderUnavailableException(ProviderName, $"HTTP {(int)response.StatusCode}: {responseBody}");
        }

        return (responseBody, response);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (_cachedAccessToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            return _cachedAccessToken;

        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cachedAccessToken is not null && _cachedTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
                return _cachedAccessToken;

            var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ConsumerKey}:{_options.ConsumerSecret}"));
            using var req = new HttpRequestMessage(HttpMethod.Get, "oauth/v1/generate?grant_type=client_credentials");
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new ProviderUnavailableException(ProviderName, "M-Pesa OAuth call failed", ex);
            }

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("M-Pesa OAuth failed: {StatusCode} {Body}", response.StatusCode, body);
                throw new ProviderUnavailableException(ProviderName, $"M-Pesa OAuth HTTP {(int)response.StatusCode}: {body}");
            }

            var token = JsonSerializer.Deserialize<MPesaOAuthResponse>(body);
            if (token is null || string.IsNullOrEmpty(token.AccessToken))
                throw new ProviderUnavailableException(ProviderName, "M-Pesa OAuth returned an empty token");

            var expiresIn = int.TryParse(token.ExpiresIn, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 3599;
            _cachedAccessToken = token.AccessToken;
            _cachedTokenExpiresUtc = DateTime.UtcNow.AddSeconds(expiresIn);
            return _cachedAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string BuildStkPassword(string shortcode, string passkey, string timestamp)
    {
        var raw = shortcode + passkey + timestamp;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
    }

    // ===== Daraja JSON shapes (internal) =====

    private sealed class MPesaOAuthResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public string? ExpiresIn { get; set; }
    }

    private sealed class MPesaStkPushResponse
    {
        [JsonPropertyName("MerchantRequestID")] public string? MerchantRequestID { get; set; }
        [JsonPropertyName("CheckoutRequestID")] public string? CheckoutRequestID { get; set; }
        [JsonPropertyName("ResponseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("ResponseDescription")] public string? ResponseDescription { get; set; }
        [JsonPropertyName("CustomerMessage")] public string? CustomerMessage { get; set; }
    }

    private sealed class MPesaReversalResponse
    {
        [JsonPropertyName("OriginatorConversationID")] public string? OriginatorConversationID { get; set; }
        [JsonPropertyName("ConversationID")] public string? ConversationID { get; set; }
        [JsonPropertyName("ResponseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("ResponseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class MPesaB2CResponse
    {
        [JsonPropertyName("OriginatorConversationID")] public string? OriginatorConversationID { get; set; }
        [JsonPropertyName("ConversationID")] public string? ConversationID { get; set; }
        [JsonPropertyName("ResponseCode")] public string? ResponseCode { get; set; }
        [JsonPropertyName("ResponseDescription")] public string? ResponseDescription { get; set; }
    }

    private sealed class MPesaCallbackEnvelope
    {
        [JsonPropertyName("Body")] public MPesaCallbackBody? Body { get; set; }

        // B2C / Reversal callbacks ship the body at the top level under "Result" — not nested in "Body".
        [JsonPropertyName("Result")] public MPesaResultCallback? Result { get; set; }
    }

    private sealed class MPesaCallbackBody
    {
        [JsonPropertyName("stkCallback")] public MPesaStkCallback? StkCallback { get; set; }
    }

    private sealed class MPesaStkCallback
    {
        [JsonPropertyName("MerchantRequestID")] public string? MerchantRequestID { get; set; }
        [JsonPropertyName("CheckoutRequestID")] public string? CheckoutRequestID { get; set; }
        [JsonPropertyName("ResultCode")] public int ResultCode { get; set; }
        [JsonPropertyName("ResultDesc")] public string? ResultDesc { get; set; }
    }

    internal sealed class MPesaResultCallback
    {
        [JsonPropertyName("ResultType")] public int ResultType { get; set; }
        [JsonPropertyName("ResultCode")] public int ResultCode { get; set; }
        [JsonPropertyName("ResultDesc")] public string? ResultDesc { get; set; }
        [JsonPropertyName("OriginatorConversationID")] public string? OriginatorConversationID { get; set; }
        [JsonPropertyName("ConversationID")] public string? ConversationID { get; set; }
        [JsonPropertyName("TransactionID")] public string? TransactionID { get; set; }
        [JsonPropertyName("ResultParameters")] public MPesaResultParameters? ResultParameters { get; set; }
    }

    internal sealed class MPesaResultParameters
    {
        [JsonPropertyName("ResultParameter")] public List<MPesaResultParameter>? ResultParameter { get; set; }
    }

    internal sealed class MPesaResultParameter
    {
        [JsonPropertyName("Key")] public string Key { get; set; } = string.Empty;
        [JsonPropertyName("Value")] public JsonElement Value { get; set; }
    }
}
