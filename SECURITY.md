# Security Policy

## Reporting a vulnerability

Email security disclosures to **security@thegeek.co.za** with subject `Bhengu.Finance.Payments security`. **Do not open a public GitHub issue.**

We acknowledge within 48 hours and aim to ship a fix within 14 days for high-severity issues. Coordinated disclosure: we'll credit you in the release notes if you want.

## Supported versions

| Version | Status |
|---|---|
| 2.0.0-preview.* | Active (current) |
| 1.0.x | Deprecated — please migrate. No security backports. |
| < 1.0 | Unsupported |

## What we never do

The SDK is built with these absolute rules. If you find a violation, that's a security bug — report it.

1. **Never log secrets.** No `ApiKey`, `Passphrase`, `SecretKey`, `WebhookSecret`, private keys, or signing keys ever appear in log output — not even at `LogLevel.Debug`.
2. **Never log PANs / CVVs / PINs.** The SDK never holds raw card data; client-side tokenisation only.
3. **Never use `Random` for crypto.** All signature material uses `System.Security.Cryptography` primitives.
4. **Never accept `HttpClient` certificate-bypass overrides.** Consumers can configure their own `HttpMessageHandler`; we don't override their TLS choices.
5. **Never bundle secrets in the package.** No keys, no certs, no merchant IDs — those come from `IConfiguration`.

## What you must do (consumer responsibilities)

The SDK can't protect you from misconfiguration. Production checklist:

### Secret storage

```csharp
// DO NOT do this (secrets in source-controlled appsettings.json)
"Bhengu": { "Finance": { "Payments": { "Stripe": { "SecretKey": "sk_live_..." } } } }

// DO this — pull from Azure Key Vault / AWS Secrets Manager / env vars
builder.Configuration.AddAzureKeyVault(...);
builder.Configuration.AddEnvironmentVariables(prefix: "BHENGU_");
```

### Webhook signature verification

**Always** verify webhook signatures before acting on the event:

```csharp
if (!provider.VerifyWebhookSignature(body, signature))
    return Results.Unauthorized();
```

Skipping this lets an attacker forge "payment succeeded" events and trigger your fulfilment pipeline.

For providers that don't HMAC (PayFast ITN, M-Pesa callbacks, some Pesapal IPNs), supplement with source-IP allowlisting from the provider's published ranges.

### IP allowlisting

For non-HMAC providers, restrict webhook endpoints to the provider's published IP ranges via your reverse proxy / API gateway:
- PayFast: see https://support.payfast.io/article/74-how-do-i-set-up-the-payfast-ip-whitelist
- M-Pesa Daraja: see https://developer.safaricom.co.ke/

### TLS

The SDK uses `HttpClient` defaults — TLS 1.2+ negotiated with the OS-supplied trust store. If you operate in a restricted environment (FIPS, etc.):

```csharp
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }
    });
});
```

### Logs and PII

- Set your logger to scrub query strings (`HttpClient` instrumentation can leak `?signature=...` URL params)
- Don't log full `PaymentRequest.Metadata` — it may contain payer email/phone
- Don't log `PaymentResponse.GatewayReference` to consumer-visible surfaces if it's a sensitive token

### CSRF on redirect-flow callbacks

Some providers (Ozow, PayJustNow, OPay, etc.) redirect the payer's browser back to your site after payment with a query-string status. **Verify these against the provider's server-side status API** before trusting the URL — a malicious payer can edit the URL.

### Idempotency

Charge endpoints in your API must be idempotent. Use a deduplication key (e.g. order ID) and require it on every charge. The SDK doesn't enforce this; your endpoint must.

## Dependency security

We track CVE feeds for our key dependencies:
- `Stripe.net`
- `Microsoft.Extensions.*`
- `System.Text.Json`
- `Polly`
- Each provider's specific SDK packages

Dependabot is configured (see `.github/dependabot.yml`) to file PRs for known CVE patches within 24h of disclosure.

## Cryptography stance

The SDK uses standard library crypto only — no custom implementations of HMAC, RSA, AES, etc. If you find us rolling our own crypto, that's a bug.

Where provider protocols require deprecated algorithms (e.g. PayFast uses MD5 for signatures, UnionPay v5.1 uses SHA-1 in places), we follow the provider's spec — those are the protocol's choice, not ours. We document the exposure in each provider's README where relevant.
