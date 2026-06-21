// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bhengu.Finance.Payments.Interswitch.Internals;

/// <summary>
/// Computes the Interswitch "InterswitchAuth" request-signing security headers.
/// <para>
/// Single source of truth so the payment provider and the shared
/// <see cref="InterswitchHttpClient"/> cannot drift. Verified against Interswitch's official
/// documentation — the sample code there builds the signature cipher, hashes it with a plain
/// SHA-512 <c>MessageDigest</c> (NOT an HMAC), Base64-encodes the digest, and uses a Unix
/// timestamp in <b>seconds</b>:
/// </para>
/// <code>
/// String signatureCipher = httpMethod + "&amp;" + encodedResourceUrl + "&amp;" + timestamp + "&amp;" + nonce + "&amp;" + clientId + "&amp;" + clientSecretKey;
/// MessageDigest messageDigest = MessageDigest.getInstance("SHA512");
/// byte[] signatureBytes = messageDigest.digest(signatureCipher.getBytes());
/// String signature = new String(Base64.encodeBase64(signatureBytes));
/// long timestamp = calendar.getTimeInMillis() / 1000; // SECONDS
/// </code>
/// Sources:
///   https://sandbox.interswitchng.com/docbase/docs/interswitch-sec-headers/sample-code/ (verbatim Java sample)
///   https://interswitch-docs.readme.io/reference/header-computation
///   https://docs.interswitchgroup.com/docs/authentication
/// </summary>
internal static class InterswitchSignature
{
    /// <summary>Value of the <c>SignatureMethod</c> header and the algorithm fed to the digest.</summary>
    /// <remarks>
    /// Interswitch's sample sets <c>String signatureMethod = "SHA512"</c> (no hyphen) and passes the
    /// same literal to both <c>MessageDigest.getInstance(...)</c> and the <c>SIGNATURE_METHOD</c> header.
    /// Source: https://sandbox.interswitchng.com/docbase/docs/interswitch-sec-headers/sample-code/
    /// </remarks>
    public const string SignatureMethod = "SHA512";

    /// <summary>The signed material with the canonical Interswitch field order: <c>method &amp; url &amp; timestamp &amp; nonce &amp; clientId &amp; clientSecret</c>.</summary>
    /// <param name="httpMethod">HTTP verb, upper-case (e.g. <c>POST</c>).</param>
    /// <param name="resourceUrl">The ABSOLUTE request URL (scheme + host + path), percent-encoded by the caller.</param>
    /// <param name="timestampSeconds">Unix timestamp in SECONDS — see <see cref="UnixTimestampSeconds"/>.</param>
    /// <param name="nonce">Per-request unique value (max 64 chars).</param>
    /// <param name="clientId">Interswitch project client id.</param>
    /// <param name="clientSecret">Interswitch project client secret (part of the signed string, NOT an HMAC key).</param>
    public static string ComputeSignature(
        string httpMethod,
        string resourceUrl,
        string timestampSeconds,
        string nonce,
        string clientId,
        string clientSecret)
    {
        // Canonical signature cipher (verbatim field order from the Interswitch Java sample):
        //   httpMethod + "&" + encodedResourceUrl + "&" + timestamp + "&" + nonce + "&" + clientId + "&" + clientSecretKey
        // Source: https://sandbox.interswitchng.com/docbase/docs/interswitch-sec-headers/sample-code/
        var signatureCipher = string.Concat(
            httpMethod, "&", resourceUrl, "&", timestampSeconds, "&", nonce, "&", clientId, "&", clientSecret);

        // Plain SHA-512 of the cipher bytes — the docs use MessageDigest.digest(), NOT an HMAC.
        var digest = SHA512.HashData(Encoding.UTF8.GetBytes(signatureCipher));

        // Base64-encode the raw digest (the docs use Base64.encodeBase64) — NOT hex.
        return Convert.ToBase64String(digest);
    }

    /// <summary>Current Unix time in SECONDS (Interswitch requires seconds, never milliseconds).</summary>
    /// <remarks>
    /// Interswitch's docs are explicit that the timestamp must be in seconds and NOT milliseconds.
    /// Source: https://interswitch-docs.readme.io/reference/header-computation
    /// </remarks>
    public static string UnixTimestampSeconds() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
