// © 2026 The Other Bhengu (Pty) Ltd t/a The Geek. Apache-2.0-licensed.

// Minimal multi-provider checkout API demonstrating the SDK's intended usage.
//
// Run:
//   cd samples/BasicCharge
//   dotnet user-secrets set "Bhengu:Finance:Payments:PayFast:MerchantId" "10000100"
//   dotnet user-secrets set "Bhengu:Finance:Payments:PayFast:MerchantKey" "46f0cd694581a"
//   dotnet user-secrets set "Bhengu:Finance:Payments:PayFast:Passphrase" "jt7NOE43FZPn"
//   dotnet user-secrets set "Bhengu:Finance:Payments:Stripe:SecretKey" "sk_test_..."
//   dotnet run
//
// Endpoints:
//   POST /charge/payfast   — PayFast charge
//   POST /charge/stripe    — Stripe charge
//   POST /webhooks/payfast — ITN webhook for PayFast
//   GET  /providers        — list registered providers + capabilities
//   GET  /health           — liveness probe

using Bhengu.Finance.Payments.Core;
using Bhengu.Finance.Payments.Core.Exceptions;
using Bhengu.Finance.Payments.Core.Interfaces;
using Bhengu.Finance.Payments.Core.Models;
using Bhengu.Finance.Payments.PayFast.Extensions;
using Bhengu.Finance.Payments.Stripe.Extensions;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// 1. Register providers. Each call validates required config at startup.
builder.Services.AddPayFastPayments(builder.Configuration);
builder.Services.AddStripePayments(builder.Configuration);

// 2. (Optional but recommended) — set a sensible HTTP timeout on all SDK HttpClients.
//    For full retry/circuit-breaker policies see docs/RESILIENCE.md. Charges are NOT
//    idempotent for most providers, so blanket retries on charge endpoints are unsafe.
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
});

// 3. (Optional) — OpenTelemetry. To enable, add `OpenTelemetry.Extensions.Hosting` +
//    `OpenTelemetry.Exporter.Console` / OTLP packages, then:
//
//    using Bhengu.Finance.Payments.Core.Observability;
//    builder.Services.AddOpenTelemetry()
//        .WithTracing(b => b.AddSource(BhenguPaymentDiagnostics.ActivitySourceName))
//        .WithMetrics(b => b.AddMeter(BhenguPaymentDiagnostics.MeterName));
//
// See docs/OBSERVABILITY.md.

var app = builder.Build();

// === Endpoints ===

// Charge — resolves the provider by name from the URL using keyed services.
app.MapPost("/charge/{provider}", async (
    string provider,
    [FromBody] PaymentRequest request,
    IEnumerable<IPaymentGatewayProvider> providers,
    ILogger<Program> logger) =>
{
    var gw = providers.FirstOrDefault(p =>
        string.Equals(p.ProviderName, provider, StringComparison.OrdinalIgnoreCase));

    if (gw is null)
        return Results.NotFound(new { error = "unknown_provider", message = $"No provider registered with name '{provider}'." });

    if (!gw.Capabilities.HasFlag(ProviderCapabilities.Charge))
        return Results.BadRequest(new { error = "capability_missing", message = $"{provider} does not support charge." });

    try
    {
        var response = await gw.ProcessPaymentAsync(request);

        // Redirect-flow providers (PayFast / Ozow / OPay etc.) return a URL to send the payer to.
        if (response.RedirectUrl is not null)
            return Results.Json(new
            {
                response.GatewayReference,
                response.Status,
                redirect = response.RedirectUrl,
                message = "Redirect the payer to the URL above to complete payment."
            });

        return Results.Json(response);
    }
    catch (PaymentDeclinedException ex)
    {
        logger.LogWarning("Payment declined by {Provider}: {Code} — {Message}",
            ex.ProviderName, ex.ProviderErrorCode, ex.ProviderErrorMessage);
        return Results.UnprocessableEntity(new { error = "declined", code = ex.ProviderErrorCode, message = ex.ProviderErrorMessage });
    }
    catch (ProviderRateLimitException)
    {
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }
    catch (ProviderUnavailableException ex)
    {
        logger.LogError(ex, "Provider {Provider} unavailable", ex.ProviderName);
        // DO NOT retry blindly — payment may have succeeded. Reconcile via webhook + status query.
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }
});

// Webhook — read body once, verify, parse, hand to your domain handler.
app.MapPost("/webhooks/{providerName}", async (
    string providerName,
    HttpContext ctx,
    IEnumerable<IPaymentGatewayProvider> providers,
    ILogger<Program> logger) =>
{
    var provider = providers.FirstOrDefault(p =>
        string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase));
    if (provider is null) return Results.NotFound();

    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();

    var signature = ctx.Request.Headers.TryGetValue("X-Signature", out var hdr)
        ? hdr.ToString()
        : ExtractSignatureFromBody(body); // some providers put it in the body (PayFast ITN does)

    if (!provider.VerifyWebhookSignature(body, signature))
    {
        logger.LogWarning("Webhook signature failed for {Provider}", providerName);
        return Results.Unauthorized();
    }

    var evt = await provider.ParseWebhookAsync(body);
    if (evt is null)
        return Results.Ok(); // unrecognised event type — ack to avoid retries

    logger.LogInformation("Webhook from {Provider}: {Reference} -> {Status}",
        providerName, evt.GatewayReference, evt.Status);

    // Your idempotent handler goes here — match on evt.GatewayReference + evt.Status.
    return Results.Ok();
});

// Provider catalogue — useful for ops dashboards and debugging registration order.
app.MapGet("/providers", (IEnumerable<IPaymentGatewayProvider> providers) =>
    providers.Select(p => new
    {
        name = p.ProviderName,
        capabilities = Enum.GetValues<ProviderCapabilities>()
            .Where(f => f != ProviderCapabilities.None && p.Capabilities.HasFlag(f))
            .Select(f => f.ToString())
    }));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// === Helpers ===

static string ExtractSignatureFromBody(string body)
{
    // PayFast posts ITN as form-urlencoded with a 'signature' field. Tolerate it appearing
    // in either order. Producer apps should prefer the header pattern where the provider supports it.
    foreach (var pair in body.Split('&'))
    {
        var kv = pair.Split('=', 2);
        if (kv.Length == 2 && kv[0].Equals("signature", StringComparison.OrdinalIgnoreCase))
            return Uri.UnescapeDataString(kv[1]);
    }
    return string.Empty;
}
