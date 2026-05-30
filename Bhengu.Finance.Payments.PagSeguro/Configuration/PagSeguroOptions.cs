// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.PagSeguro.Configuration;

/// <summary>
/// Configuration for the PagSeguro (PagBank) provider. Bound from <c>Bhengu:Finance:Payments:PagSeguro</c>.
/// </summary>
public sealed class PagSeguroOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:PagSeguro";

    /// <summary>PagBank API token. Used as the Bearer token on every request.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>Webhook signing secret used to verify HMAC-SHA256 of inbound webhook payloads.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Optional default notification URL the merchant has registered for order callbacks.</summary>
    public string? NotificationUrl { get; set; }

    /// <summary>ISO 4217 currency code used when the request does not specify one (defaults to BRL).</summary>
    public string Currency { get; set; } = "BRL";

    /// <summary>When true, requests are routed to the PagBank sandbox base URL.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the production base URL. Leave null in normal use (defaults to https://api.pagseguro.com).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>Override the sandbox base URL. Leave null in normal use (defaults to https://sandbox.api.pagseguro.com).</summary>
    public string? SandboxUrl { get; set; }
}
