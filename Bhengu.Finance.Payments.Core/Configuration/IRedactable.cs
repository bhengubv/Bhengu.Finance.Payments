// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.Core.Configuration;

/// <summary>
/// Marker interface for option types whose properties contain secrets (API keys, signing secrets,
/// private keys). Implementations should override <see cref="object.ToString"/> to return a
/// redacted summary so the values never leak to logs even at <c>LogLevel.Debug</c>.
///
/// <para>Recommended pattern:</para>
/// <code>
/// public sealed class StripeOptions : IRedactable
/// {
///     public string SecretKey { get; set; } = "";
///     public override string ToString() =&gt; $"StripeOptions {{ SecretKey=***{SecretKey.LastN(4)} }}";
/// }
/// </code>
///
/// The Bhengu.Finance.Payments SDK never logs option values directly. This interface exists so
/// consumer infrastructure (DI logging, options diagnostics) can call <c>ToString</c> safely.
/// </summary>
public interface IRedactable
{
    /// <summary>Return a log-safe representation of this options object. Never echo raw secrets.</summary>
    string ToRedactedString();
}

/// <summary>Helpers for safely formatting secret-bearing strings in logs.</summary>
public static class RedactionExtensions
{
    /// <summary>
    /// Returns the last <paramref name="count"/> characters of a secret, or "***" if the secret is
    /// too short to safely show any portion. Suitable for log breadcrumbs ("which key did we load?").
    /// </summary>
    public static string LastN(this string? secret, int count = 4)
    {
        if (string.IsNullOrEmpty(secret)) return "(empty)";
        if (secret.Length <= count * 2) return "***";
        return $"***{secret[^count..]}";
    }
}
