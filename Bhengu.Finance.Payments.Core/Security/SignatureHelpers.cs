// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bhengu.Finance.Payments.Core.Security;

/// <summary>
/// Constant-time signature helpers shared by every provider that verifies webhook payloads.
/// Centralising these eliminates the four classes of bug that otherwise sneak in:
///   (1) provider X uses string equality on hex signatures — timing-attack vulnerable
///   (2) provider Y forgets the lowercase / uppercase normalisation before comparing
///   (3) provider Z encodes the HMAC as Base64 in production but its tests use hex
///   (4) provider W doesn't validate signature length before slicing — DoS via short input
///
/// All comparisons use <see cref="CryptographicOperations.FixedTimeEquals"/> so attackers cannot
/// learn anything by measuring how long the verification takes.
/// </summary>
public static class SignatureHelpers
{
    /// <summary>How the upstream provider encodes the signature on the wire.</summary>
    public enum Encoding
    {
        /// <summary>Lowercase hex (most providers — Paystack, Razorpay, Flutterwave HMAC schemes).</summary>
        HexLower,
        /// <summary>Uppercase hex (PayFast MD5, some legacy CIS providers).</summary>
        HexUpper,
        /// <summary>Standard Base64 (Stripe Webhook v1, Paystack v2).</summary>
        Base64
    }

    /// <summary>Verify an HMAC-SHA256 signature in constant time.</summary>
    public static bool VerifyHmacSha256(string payload, string signature, string secret, Encoding encoding = Encoding.HexLower)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;
        var computed = HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(payload));
        return CompareEncoded(computed, signature, encoding);
    }

    /// <summary>Verify an HMAC-SHA512 signature in constant time.</summary>
    public static bool VerifyHmacSha512(string payload, string signature, string secret, Encoding encoding = Encoding.HexLower)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;
        var computed = HMACSHA512.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(payload));
        return CompareEncoded(computed, signature, encoding);
    }

    /// <summary>Verify an HMAC-SHA1 signature in constant time. Only for legacy providers — avoid for new integrations.</summary>
    public static bool VerifyHmacSha1(string payload, string signature, string secret, Encoding encoding = Encoding.HexLower)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(secret)) return false;
        var computed = HMACSHA1.HashData(System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(payload));
        return CompareEncoded(computed, signature, encoding);
    }

    /// <summary>
    /// Verify a PayFast-style MD5 signature in constant time. The canonical string is the sorted
    /// form parameter pairs (key=URL-encoded-value joined with &amp;) + "&amp;passphrase=urlenc(passphrase)".
    /// </summary>
    public static bool VerifyMd5(string canonicalString, string signature, Encoding encoding = Encoding.HexLower)
    {
        ArgumentNullException.ThrowIfNull(canonicalString);
        if (string.IsNullOrEmpty(signature)) return false;
        var computed = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(canonicalString));
        return CompareEncoded(computed, signature, encoding);
    }

    /// <summary>
    /// Verify an RSA-SHA256 signature against a public key (Alipay, WeChat Pay v3, UnionPay).
    /// Throws <see cref="CryptographicException"/> if the key is malformed.
    /// </summary>
    public static bool VerifyRsaSha256(string payload, string signature, RSA publicKey, Encoding encoding = Encoding.Base64)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(publicKey);
        if (string.IsNullOrEmpty(signature)) return false;
        var sigBytes = DecodeSignature(signature, encoding);
        if (sigBytes is null) return false;
        return publicKey.VerifyData(
            System.Text.Encoding.UTF8.GetBytes(payload),
            sigBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
    }

    /// <summary>Constant-time byte-string comparison. Always returns false if lengths differ.</summary>
    public static bool ConstantTimeEquals(string a, string b)
    {
        if (a is null || b is null) return false;
        if (a.Length != b.Length) return false;
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private static bool CompareEncoded(byte[] computed, string signature, Encoding encoding)
    {
        var sigBytes = DecodeSignature(signature, encoding);
        if (sigBytes is null) return false;
        if (sigBytes.Length != computed.Length) return false;
        return CryptographicOperations.FixedTimeEquals(computed, sigBytes);
    }

    private static byte[]? DecodeSignature(string signature, Encoding encoding)
    {
        try
        {
            return encoding switch
            {
                Encoding.HexLower => HexDecode(signature.ToLowerInvariant()),
                Encoding.HexUpper => HexDecode(signature.ToUpperInvariant()),
                Encoding.Base64 => Convert.FromBase64String(signature),
                _ => null
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? HexDecode(string hex)
    {
        if (hex.Length % 2 != 0) return null;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }
}
