// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.PayFast.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bhengu.Finance.Payments.PayFast.Builders;

/// <summary>
/// Builds signed PayFast browser-redirect URLs for once-off, subscription, and donation flows.
/// This is distinct from <see cref="Providers.PayFastPaymentProvider"/> — the form builder is for the
/// hosted-checkout / redirect protocol, while the provider handles server-to-server ad-hoc charging.
/// </summary>
public sealed class PayFastFormBuilder
{
    // PayFast's official field order for signature generation (from the official PHP SDK Auth.php).
    private static readonly string[] FieldOrder =
    [
        "merchant_id", "merchant_key", "return_url", "cancel_url", "notify_url",
        "notify_method", "name_first", "name_last", "email_address", "cell_number",
        "m_payment_id", "amount", "item_name", "item_description",
        "custom_int1", "custom_int2", "custom_int3", "custom_int4", "custom_int5",
        "custom_str1", "custom_str2", "custom_str3", "custom_str4", "custom_str5",
        "email_confirmation", "confirmation_address", "currency", "payment_method",
        "subscription_type", "passphrase",
        "billing_date", "recurring_amount", "frequency", "cycles",
        "subscription_notify_email", "subscription_notify_webhook", "subscription_notify_buyer"
    ];

    private readonly PayFastOptions _options;
    private readonly ILogger<PayFastFormBuilder> _logger;

    public PayFastFormBuilder(IOptions<PayFastOptions> options, ILogger<PayFastFormBuilder> logger)
    {
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Build a PayFast redirect URL for a once-off payment. Amount in major currency unit (rand).</summary>
    public string BuildOnceOffPaymentUrl(
        string mPaymentId,
        decimal amount,
        string itemName,
        string? description = null,
        string? emailAddress = null,
        string? returnUrl = null,
        string? cancelUrl = null,
        string? customStr1 = null,
        string? customStr2 = null,
        string? nameFirst = null,
        string? nameLast = null,
        string? cellNumber = null,
        string currency = "ZAR")
    {
        var formData = new Dictionary<string, string>
        {
            ["merchant_id"] = _options.MerchantId,
            ["merchant_key"] = _options.MerchantKey,
            ["return_url"] = returnUrl ?? _options.ReturnUrl ?? "",
            ["cancel_url"] = cancelUrl ?? _options.CancelUrl ?? "",
            ["notify_url"] = _options.NotifyUrl ?? "",
            ["name_first"] = nameFirst ?? "",
            ["name_last"] = nameLast ?? "",
            ["email_address"] = emailAddress ?? "",
            ["cell_number"] = cellNumber ?? "",
            ["m_payment_id"] = mPaymentId,
            ["amount"] = amount.ToString("F2", CultureInfo.InvariantCulture),
            ["item_name"] = itemName,
            ["item_description"] = description ?? "",
            ["custom_str1"] = customStr1 ?? "",
            ["custom_str2"] = customStr2 ?? "",
            ["currency"] = currency
        };

        if (!string.IsNullOrEmpty(emailAddress))
        {
            formData["email_confirmation"] = "1";
            formData["confirmation_address"] = emailAddress;
        }

        formData["signature"] = GenerateSignature(formData);

        var redirectUrl = $"{GetBaseUrl()}/eng/process?{BuildQueryString(formData)}";
        _logger.LogInformation("Generated once-off payment URL m_payment_id={MPaymentId} amount={Amount}", mPaymentId, amount);
        return redirectUrl;
    }

    /// <summary>
    /// Build a PayFast redirect URL for a subscription (recurring) payment.
    /// Frequency: 3=monthly, 4=quarterly, 5=biannual, 6=annual. Cycles: 0 = indefinite.
    /// </summary>
    public string BuildSubscriptionUrl(
        string mPaymentId,
        decimal recurringAmount,
        int frequency,
        int cycles = 0,
        string? itemName = null,
        string? description = null,
        string? emailAddress = null,
        string? returnUrl = null,
        string? cancelUrl = null,
        string currency = "ZAR")
    {
        if (frequency is < 1 or > 6)
        {
            _logger.LogWarning("Invalid PayFast frequency {Frequency} — defaulting to 3 (monthly). Valid: 1=Daily, 2=Weekly, 3=Monthly, 4=Quarterly, 5=Biannually, 6=Annual", frequency);
            frequency = 3;
        }

        var formData = new Dictionary<string, string>
        {
            ["merchant_id"] = _options.MerchantId,
            ["merchant_key"] = _options.MerchantKey,
            ["return_url"] = returnUrl ?? _options.ReturnUrl ?? "",
            ["cancel_url"] = cancelUrl ?? _options.CancelUrl ?? "",
            ["notify_url"] = _options.NotifyUrl ?? "",
            ["email_address"] = emailAddress ?? "",
            ["m_payment_id"] = mPaymentId,
            ["amount"] = recurringAmount.ToString("F2", CultureInfo.InvariantCulture),
            ["item_name"] = itemName ?? "Subscription",
            ["item_description"] = description ?? "",
            ["currency"] = currency,
            ["subscription_type"] = "1",
            ["billing_date"] = DateTime.UtcNow.AddMonths(1).ToString("yyyy-MM-dd"),
            ["recurring_amount"] = recurringAmount.ToString("F2", CultureInfo.InvariantCulture),
            ["frequency"] = frequency.ToString(),
            ["cycles"] = cycles.ToString()
        };

        formData["signature"] = GenerateSignature(formData);

        var redirectUrl = $"{GetBaseUrl()}/eng/process?{BuildQueryString(formData)}";
        _logger.LogInformation("Generated subscription URL m_payment_id={MPaymentId} amount={Amount} frequency={Frequency}",
            mPaymentId, recurringAmount, frequency);
        return redirectUrl;
    }

    /// <summary>Build a PayFast tokenisation-only URL (R0 ad-hoc agreement creation).</summary>
    public string BuildTokenisationUrl(
        string returnUrl,
        string cancelUrl,
        string notifyUrl,
        string? email = null,
        string? cellNumber = null)
    {
        var formData = new Dictionary<string, string>
        {
            ["merchant_id"] = _options.MerchantId,
            ["return_url"] = returnUrl,
            ["cancel_url"] = cancelUrl,
            ["notify_url"] = notifyUrl,
            ["subscription_type"] = "2",
            ["amount"] = "0",
            ["item_name"] = "Card Tokenisation"
        };

        if (!string.IsNullOrEmpty(email)) formData["email_address"] = email;
        if (!string.IsNullOrEmpty(cellNumber)) formData["cell_number"] = cellNumber;

        formData["signature"] = GenerateSignature(formData);

        return $"{GetBaseUrl()}/eng/process?{BuildQueryString(formData)}";
    }

    /// <summary>
    /// Build the link a buyer follows to update the card behind a tokenised agreement / subscription
    /// (PayFast's <c>/eng/recurring/update/{token}</c>). Not available in sandbox.
    /// </summary>
    public string BuildCardUpdateUrl(string token, string? returnUrl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        if (_options.UseSandbox)
            throw new BhenguPaymentException(ProviderNames.PayFast,
                "PayFast card-update links are not available in sandbox mode.", "card_update_sandbox_unsupported");

        var url = $"{GetBaseUrl()}/eng/recurring/update/{Uri.EscapeDataString(token)}";
        if (!string.IsNullOrEmpty(returnUrl))
            url += $"?return={Uri.EscapeDataString(returnUrl)}";
        return url;
    }

    /// <summary>The PayFast Onsite (in-page popup) process endpoint for the current environment.</summary>
    public string OnsiteProcessUrl => $"{GetBaseUrl()}/onsite/process";

    /// <summary>
    /// Build the signed, form-encoded request body for an Onsite (in-page popup) payment. POST this body
    /// to <see cref="OnsiteProcessUrl"/> to obtain a payment UUID. Amount is in rands (major unit).
    /// Used by <see cref="Providers.PayFastOnsiteProvider"/>.
    /// </summary>
    public string BuildOnsitePaymentBody(
        string mPaymentId, decimal amount, string itemName, string? description = null,
        string? emailAddress = null, string? returnUrl = null, string? cancelUrl = null,
        string? nameFirst = null, string? nameLast = null, string? cellNumber = null,
        string? customStr1 = null, string? customStr2 = null, string currency = "ZAR")
    {
        var formData = new Dictionary<string, string>
        {
            ["merchant_id"] = _options.MerchantId,
            ["merchant_key"] = _options.MerchantKey,
            ["return_url"] = returnUrl ?? _options.ReturnUrl ?? "",
            ["cancel_url"] = cancelUrl ?? _options.CancelUrl ?? "",
            ["notify_url"] = _options.NotifyUrl ?? "",
            ["name_first"] = nameFirst ?? "",
            ["name_last"] = nameLast ?? "",
            ["email_address"] = emailAddress ?? "",
            ["cell_number"] = cellNumber ?? "",
            ["m_payment_id"] = mPaymentId,
            ["amount"] = amount.ToString("F2", CultureInfo.InvariantCulture),
            ["item_name"] = itemName,
            ["item_description"] = description ?? "",
            ["custom_str1"] = customStr1 ?? "",
            ["custom_str2"] = customStr2 ?? "",
            ["currency"] = currency
        };

        formData["signature"] = GenerateSignature(formData);
        return BuildQueryString(formData);
    }

    private string GetBaseUrl() => _options.UseSandbox
        ? (_options.SandboxUrl ?? "https://sandbox.payfast.co.za")
        : (_options.BaseUrl ?? "https://www.payfast.co.za");

    private string GenerateSignature(Dictionary<string, string> formData)
    {
        var dataWithPassphrase = new Dictionary<string, string>(formData);
        if (!string.IsNullOrEmpty(_options.Passphrase))
            dataWithPassphrase["passphrase"] = _options.Passphrase;

        var parts = new List<string>();
        foreach (var field in FieldOrder)
        {
            if (dataWithPassphrase.TryGetValue(field, out var value) && !string.IsNullOrEmpty(value))
                parts.Add($"{field}={WebUtility.UrlEncode(value.Trim())}");
        }

        var signatureString = string.Join("&", parts);
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(signatureString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string BuildQueryString(Dictionary<string, string> formData)
    {
        var parts = new List<string>();

        foreach (var field in FieldOrder)
        {
            if (formData.TryGetValue(field, out var value) && !string.IsNullOrEmpty(value))
                parts.Add($"{field}={WebUtility.UrlEncode(value.Trim())}");
        }

        if (formData.TryGetValue("signature", out var sig))
            parts.Add($"signature={sig}");

        return string.Join("&", parts);
    }
}
