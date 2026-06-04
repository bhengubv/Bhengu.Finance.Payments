// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bhengu.Finance.Payments.IPay.Internals;

/// <summary>Shared HMAC helper for iPay's HMAC-SHA256-hex hash scheme.</summary>
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
}
