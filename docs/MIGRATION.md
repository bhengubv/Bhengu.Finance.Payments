# Migration guide

## 1.x → 2.0

The 1.x line (released as `1.0.32` and earlier) shipped stub providers (`PayFastService`, `BricsPayService`, `ApplePayService`, `GoogleService`) that returned hardcoded strings like `"Simulated PayFast payment response"`. The 2.x line replaces those with real implementations behind a typed contract.

If you depended on the 1.x stubs, you weren't running real payments — there's no consumer-side breaking change. Just install the new packages and follow the [Quickstart](../README.md#quickstart--single-provider).

## 2.0.0-preview.1 → 2.0.0-preview.2

Breaking but mechanical.

### `IPaymentGatewayProvider` gained a property

If you implement the interface yourself, add a `Capabilities` property:

```csharp
public ProviderCapabilities Capabilities =>
    ProviderCapabilities.Charge |
    ProviderCapabilities.Refund |
    ProviderCapabilities.Webhook |
    ProviderCapabilities.Cards;
```

Consumers don't need to change anything — `IPaymentGatewayProvider` is normally injected, not implemented.

### `PaymentResponse.Message` is no longer overloaded with URLs

If you read the response message looking for a URL, switch to `RedirectUrl`:

```csharp
// Before — fragile string-parsing
if (response.Message.StartsWith("Redirect to: "))
{
    var url = response.Message["Redirect to: ".Length..];
    return Results.Redirect(url);
}

// After — first-class property
if (response.RedirectUrl is not null)
    return Results.Redirect(response.RedirectUrl);
```

### DI: inject by name to avoid the "whichever was last registered" bug

Single-provider apps work as before:

```csharp
app.MapPost("/charge", async (IPaymentGatewayProvider provider) => { ... });
```

If you register **more than one provider**, the bare injection silently returns "whichever was registered last" — DI default behaviour. Switch to keyed services or LINQ:

```csharp
// Option A: keyed services (recommended)
app.MapPost("/charge/payfast", async (
    [FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider provider) =>
{ ... });

// Option B: LINQ filter
app.MapPost("/charge/{provider}", async (
    string provider,
    IEnumerable<IPaymentGatewayProvider> providers) =>
{
    var gw = providers.First(p => p.ProviderName == provider);
    // ...
});
```

### Apple Pay / Google Pay now fail at startup, not first request

Misconfiguration that used to surface as a `ProviderConfigurationException` on the first Apple Pay request now surfaces at **app startup**. If your Apple Pay options name a `DownstreamProcessor` that isn't registered, your `WebApplication.Run()` call crashes immediately with a clear error message.

This is intentional — fail fast at boot, not at customer #1.

To preserve old behaviour (don't ever do this in production), remove the `AddBhenguPaymentStartupValidation()` registration that `AddApplePayPayments()` adds. Not recommended.

### `VerifyWebhookSignature` no longer throws on missing config

Previously some providers (PayShap, BricsPay) threw `ProviderConfigurationException` if their webhook secret wasn't configured. Now uniformly across all 46 providers: log a warning + return `false`.

If you wrapped these calls in `try/catch (ProviderConfigurationException)`, you can simplify to checking the bool.

## Beyond the SDK

For per-provider migration notes (Stripe.net updates, M-Pesa Daraja v2 → v3, etc.) see each package's README.
