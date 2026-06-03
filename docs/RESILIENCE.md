# Resilience

The SDK has no built-in retry / circuit-breaker policies. By design — you decide what to retry and when.

## Recommended baseline (.NET 8+)

Use `Microsoft.Extensions.Http.Resilience` to add the standard resilience handler to every Bhengu.Finance.Payments `HttpClient`:

```sh
dotnet add package Microsoft.Extensions.Http.Resilience
```

```csharp
builder.Services.AddPayFastPayments(builder.Configuration);
builder.Services.AddStripePayments(builder.Configuration);
// ... other AddXxxPayments

// Apply standard resilience (retry + circuit-breaker + timeout + rate-limiter) to all Bhengu HttpClients.
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.AddStandardResilienceHandler(opts =>
    {
        // Idempotent reads are safe to retry. Charges are NOT — never enable RetryPolicy on POST
        // without an idempotency key.
        opts.Retry.MaxRetryAttempts = 3;
        opts.Retry.BackoffType = DelayBackoffType.Exponential;
        opts.Retry.UseJitter = true;
        opts.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
        opts.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
    });
});
```

## The retry problem (read this)

**Payment charges are not idempotent unless the provider supports an idempotency key**, and most don't expose this in the SDK contract today. A naive retry on `ProviderUnavailableException` can cause **double-charging** when the request actually succeeded but the response was lost in transit.

Safer pattern:
1. **For `ProcessPaymentAsync`:** retry ONLY on `ProviderUnavailableException` where you know the request couldn't have reached the provider (DNS failures, immediate connection refused). Treat any other failure as "outcome unknown — reconcile via webhook + provider status API."
2. **For `ProcessRefundAsync`:** safer to retry — most providers de-dupe by gateway reference + amount.
3. **For `ProcessPayoutAsync`:** treat like a charge; reconcile via status query, don't retry.

The SDK's typed exceptions help you make this decision:

```csharp
try
{
    var response = await provider.ProcessPaymentAsync(request);
}
catch (ProviderRateLimitException ex)
{
    // OK to retry with backoff; provider hasn't processed the charge.
    await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfterSeconds ?? 5));
    // Retry...
}
catch (ProviderUnavailableException)
{
    // UNSAFE to retry blindly. Reconcile via webhook + provider status API.
    // Enqueue a reconciliation job.
}
catch (PaymentDeclinedException)
{
    // Terminal. Do NOT retry. Show the payer a clean error.
}
```

## Circuit-breaker per provider

If one provider is having a bad day, isolate it so it doesn't slow down your overall checkout flow:

```csharp
http.AddStandardResilienceHandler(opts =>
{
    opts.CircuitBreaker.FailureRatio = 0.5;       // open after 50% failures
    opts.CircuitBreaker.MinimumThroughput = 20;   // ...over at least 20 requests
    opts.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
    opts.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(60);
});
```

While a provider's circuit is open, your endpoint can fall back to a secondary provider using the SDK's keyed-services pattern:

```csharp
app.MapPost("/charge", async (
    PaymentRequest request,
    [FromKeyedServices(ProviderNames.PayFast)] IPaymentGatewayProvider primary,
    [FromKeyedServices(ProviderNames.Yoco)] IPaymentGatewayProvider fallback) =>
{
    try { return Results.Ok(await primary.ProcessPaymentAsync(request)); }
    catch (ProviderUnavailableException)
    {
        return Results.Ok(await fallback.ProcessPaymentAsync(request));
    }
});
```

## Timeouts

Default `HttpClient` timeout is 100 seconds — too long for payments. Set it explicitly per provider or globally:

```csharp
builder.Services.ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30));
});
```

## Bulkhead / parallelism

If you have a high-traffic checkout and worry about one provider's slowness saturating your thread pool, add a bulkhead:

```csharp
http.AddStandardResilienceHandler(opts =>
{
    opts.RateLimiter.QueueLimit = 100;
    opts.RateLimiter.PermitLimit = 50;
});
```
