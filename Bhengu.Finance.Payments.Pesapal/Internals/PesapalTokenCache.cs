// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Pesapal.Internals;

/// <summary>
/// Shared OAuth-token cache for the Pesapal sibling providers. Pesapal tokens live ~5 minutes;
/// we proactively refresh at 4.5 minutes.
/// </summary>
public sealed class PesapalTokenCache
{
    private string? _token;
    private DateTime _expiresAtUtc;
    private readonly object _lock = new();

    /// <summary>Get the current cached token, or null if expired / never set.</summary>
    public string? Get()
    {
        lock (_lock)
        {
            return string.IsNullOrEmpty(_token) || DateTime.UtcNow >= _expiresAtUtc ? null : _token;
        }
    }

    /// <summary>Set the cached token with a TTL (defaults to 4.5 minutes).</summary>
    public void Set(string token, TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        lock (_lock)
        {
            _token = token;
            _expiresAtUtc = DateTime.UtcNow.Add(ttl ?? TimeSpan.FromSeconds(270));
        }
    }
}
