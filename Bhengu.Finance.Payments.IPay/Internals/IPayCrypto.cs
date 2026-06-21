// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bhengu.Finance.Payments.IPay.Internals;

/// <summary>Shared HMAC helpers for iPay: HMAC-SHA256-hex (REST API) and HMAC-SHA1-hex (web redirect flow).</summary>
internal static class IPayCrypto
{
    /// <summary>Compute lowercase-hex HMAC-SHA256 over <paramref name="data"/> keyed by <paramref name="key"/>.</summary>
    public static string ComputeHmacHex(string data, string key)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// Compute lowercase-hex HMAC-SHA1 over <paramref name="data"/> keyed by <paramref name="key"/>.
    /// iPay's web (redirect) integration mandates HMAC-SHA1 for the <c>hsh</c> field; the REST API
    /// uses HMAC-SHA256 (<see cref="ComputeHmacHex"/>). The algorithm is dictated by iPay's published
    /// protocol, not chosen by us.
    /// </summary>
    public static string ComputeHmacSha1Hex(string data, string key)
    {
#pragma warning disable CA5350 // iPay's published web-redirect protocol requires HMAC-SHA1.
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(key));
#pragma warning restore CA5350
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
