// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Bhengu.Finance.Payments.PayFast.Internals;

/// <summary>
/// PayFast signature helper shared by the redirect-flow builder, the API request signer, and the
/// subscription / mandate providers.
/// </summary>
/// <remarks>
/// PayFast signs requests two distinct ways. The <em>browser redirect</em> form-post uses MD5 over
/// the field set in PayFast's canonical order (see <c>PayFastFormBuilder.FieldOrder</c>), URL-encoded
/// values, optionally appended with the passphrase. The <em>authenticated REST</em> API instead
/// requires MD5 over the merge of the merchant header (merchant-id, version, timestamp) plus the
/// request body, sorted lexicographically ordinally and URL-encoded — that is the algorithm
/// implemented here.
/// </remarks>
internal static class PayFastSignatureHelper
{
    /// <summary>
    /// Compute the PayFast API signature for an authenticated REST request.
    /// </summary>
    /// <param name="merchantId">PayFast merchant ID.</param>
    /// <param name="passphrase">PayFast passphrase (may be empty when the merchant has signing disabled).</param>
    /// <param name="timestamp">RFC 3339 timestamp for the request (also sent as the <c>timestamp</c> header).</param>
    /// <param name="bodyParams">Form/body parameters being signed. Pass an empty dictionary for GETs.</param>
    /// <returns>The lowercase hex MD5 signature.</returns>
    public static string ComputeApiSignature(
        string merchantId,
        string passphrase,
        string timestamp,
        IDictionary<string, string> bodyParams)
    {
        ArgumentNullException.ThrowIfNull(merchantId);
        ArgumentNullException.ThrowIfNull(passphrase);
        ArgumentNullException.ThrowIfNull(timestamp);
        ArgumentNullException.ThrowIfNull(bodyParams);

        var allParams = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["merchant-id"] = merchantId,
            ["passphrase"] = passphrase,
            ["timestamp"] = timestamp,
            ["version"] = "v1"
        };
        foreach (var (k, v) in bodyParams)
            allParams[k] = v;

        var sorted = allParams
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value ?? string.Empty)}");

        var paramString = string.Join("&", sorted);
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(paramString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Compute the PayFast browser-redirect signature for the hosted-checkout flow.
    /// Pairs with <see cref="Builders.PayFastFormBuilder"/>'s field-order convention.
    /// </summary>
    /// <param name="formData">All form fields excluding <c>signature</c>.</param>
    /// <param name="passphrase">Passphrase appended after the form fields when non-empty.</param>
    /// <returns>The lowercase hex MD5 signature.</returns>
    public static string ComputeRedirectSignature(
        IReadOnlyDictionary<string, string> formData,
        string passphrase)
    {
        ArgumentNullException.ThrowIfNull(formData);
        ArgumentNullException.ThrowIfNull(passphrase);

        var parts = formData
            .Where(kv => !string.IsNullOrEmpty(kv.Value))
            .Select(kv => $"{kv.Key}={WebUtility.UrlEncode(kv.Value.Trim())}")
            .ToList();
        if (!string.IsNullOrEmpty(passphrase))
            parts.Add($"passphrase={WebUtility.UrlEncode(passphrase.Trim())}");

        var paramString = string.Join("&", parts);
        var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(paramString));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
