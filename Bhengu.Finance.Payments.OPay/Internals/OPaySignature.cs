// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bhengu.Finance.Payments.OPay.Internals;

/// <summary>
/// OPay signing primitives, centralised so the payment provider, the shared HTTP helper and the
/// tests all produce byte-identical signatures.
///
/// <para><b>Two distinct schemes — do not conflate them.</b></para>
///
/// <para><b>1. Request signing (server-to-server APIs: refund, status, …).</b>
/// HMAC-SHA512 (lowercase hex) of the request body, where the body is first sorted by its top-level
/// keys in alphabetical order, signed with the merchant secret/private key. Carried as
/// <c>Authorization: Bearer {signature}</c> + a <c>MerchantId</c> header.
/// Source (verbatim): https://documentation.opaycheckout.com/api-signature —
/// "Sort your request payload JSON according to the alphabetical order of the request keys.
///  Sign the sorted JSON with your secret key using HMAC SHA-512 algorithm." (PHP: <c>hash_hmac('sha512', $data, $secretKey)</c>).
/// NOTE: the hosted Cashier <c>cashier/create</c> call is the exception — it authenticates with
/// <c>Authorization: Bearer {PublicKey}</c> and is NOT request-signed
/// (https://documentation.opaycheckout.com/cashier-create).</para>
///
/// <para><b>2. Callback (webhook) verification.</b>
/// OPay raises a <c>sha512</c> value that is an <b>HMAC-SHA3-512</b> (hex) of a fixed format string
/// built from eight fields of the callback payload, signed with the merchant private key.
/// Source (verbatim): https://documentation.opaycheckout.com/callback-signature —
/// "Valid callbacks are raised with <c>sha512</c> value, which is essentially a HMAC-SHA3-512
///  signature of the callback payload signed using your Private Key." (PHP: <c>hash_hmac('sha3-512', $authJson, $secretKey)</c>).</para>
/// </summary>
internal static class OPaySignature
{
    /// <summary>
    /// Re-serialise a JSON object with its top-level properties ordered alphabetically (ordinal),
    /// matching OPay's "sort the request keys" rule. Non-object JSON (arrays, scalars, empty) is
    /// returned unchanged. Nested objects are preserved as-is — OPay's documented examples sort only
    /// the top-level keys.
    /// </summary>
    public static string CanonicaliseForSigning(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return json;

        var ordered = doc.RootElement.EnumerateObject()
            .OrderBy(static p => p.Name, StringComparer.Ordinal);

        using var buffer = new MemoryStream();
        // No indentation: the signed string must be the compact JSON OPay receives on the wire.
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var prop in ordered)
                prop.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>HMAC-SHA512 of <paramref name="data"/> using <paramref name="key"/>, lowercase hex.</summary>
    public static string HmacSha512Hex(string data, string key)
    {
        var mac = HMACSHA512.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>
    /// True when the host runtime can compute HMAC-SHA3-512 (required to verify OPay callbacks).
    /// SHA-3 is unavailable on older Windows CNG / OpenSSL &lt; 1.1.1 — callers must degrade safely
    /// (log + reject) rather than throw when this is false.
    /// </summary>
    public static bool IsSha3Available => HMACSHA3_512.IsSupported;

    /// <summary>
    /// Build the exact "sign content" OPay HMACs for a callback, from the eight payload fields.
    /// Format (verbatim from the docs' PHP/Java examples):
    /// <c>{Amount:"%s",Currency:"%s",Reference:"%s",Refunded:%s,Status:"%s",Timestamp:"%s",Token:"%s",TransactionID:"%s"}</c>
    /// where <c>Refunded</c> is the literal <c>t</c> or <c>f</c> (unquoted).
    /// Source: https://documentation.opaycheckout.com/callback-signature
    /// </summary>
    public static string BuildCallbackSignContent(
        string amount, string currency, string reference, bool refunded,
        string status, string timestamp, string token, string transactionId)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{{Amount:\"{0}\",Currency:\"{1}\",Reference:\"{2}\",Refunded:{3},Status:\"{4}\",Timestamp:\"{5}\",Token:\"{6}\",TransactionID:\"{7}\"}}",
            amount, currency, reference, refunded ? "t" : "f", status, timestamp, token, transactionId);

    /// <summary>
    /// HMAC-SHA3-512 of <paramref name="signContent"/> using <paramref name="privateKey"/>, lowercase hex.
    /// Throws <see cref="PlatformNotSupportedException"/> on runtimes without SHA-3 — guard with
    /// <see cref="IsSha3Available"/> first.
    /// </summary>
    public static string HmacSha3_512Hex(string signContent, string privateKey)
    {
        var mac = HMACSHA3_512.HashData(Encoding.UTF8.GetBytes(privateKey), Encoding.UTF8.GetBytes(signContent));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }
}
