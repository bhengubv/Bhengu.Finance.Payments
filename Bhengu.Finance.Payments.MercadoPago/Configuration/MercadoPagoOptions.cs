// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

namespace Bhengu.Finance.Payments.MercadoPago.Configuration;

/// <summary>
/// Configuration for the Mercado Pago provider. Bound from <c>Bhengu:Finance:Payments:MercadoPago</c> in IConfiguration.
/// </summary>
public sealed class MercadoPagoOptions
{
    public const string ConfigSection = "Bhengu:Finance:Payments:MercadoPago";

    /// <summary>
    /// Mercado Pago access token. Production tokens are prefixed <c>APP_USR-</c>; test tokens start with <c>TEST-</c>.
    /// Used as the Bearer token on every request.
    /// </summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Mercado Pago public key — used by client-side Checkout Bricks to tokenise cards.</summary>
    public string? PublicKey { get; set; }

    /// <summary>Webhook signing secret used to verify the HMAC of inbound IPN/webhook payloads.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Optional notification URL the merchant has registered with Mercado Pago for IPN callbacks.</summary>
    public string? NotificationUrl { get; set; }

    /// <summary>ISO 4217 currency code used when the PaymentRequest does not specify one (defaults to BRL).</summary>
    public string Currency { get; set; } = "BRL";

    /// <summary>When true, requests are routed against the Mercado Pago sandbox via test access tokens. Mercado Pago itself uses the same base URL for both.</summary>
    public bool UseSandbox { get; set; }

    /// <summary>Override the Mercado Pago base URL. Leave null in normal use (defaults to https://api.mercadopago.com).</summary>
    public string? BaseUrl { get; set; }
}
