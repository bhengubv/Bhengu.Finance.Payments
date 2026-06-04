# Bhengu.Finance.Payments.Redis

Redis-backed implementation of `IBhenguDistributedCache` for the Bhengu Finance Payments SDK.
Swap the default process-local in-memory cache for a Redis instance so idempotency-key dedup
caches and plan/split definition stores survive restarts and stay consistent across replicas.

## Why this package exists

The default cache for the SDK is `InMemoryBhenguDistributedCache` (process-local). That works for
single-replica deployments but loses state on every restart and doesn't share between replicas.
This package registers a Redis-backed cache that takes over the existing
`IBhenguDistributedCache` slot via `IServiceCollection.AddBhenguRedisCache(configuration)`.

## Install

```bash
dotnet add package Bhengu.Finance.Payments.Redis
```

## Usage

`appsettings.json`:

```json
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Redis": {
          "ConnectionString": "redis-prod.internal:6379,abortConnect=false",
          "KeyPrefix": "bhengu:payments:"
        }
      }
    }
  }
}
```

`Program.cs`:

```csharp
using Bhengu.Finance.Payments.Redis;

builder.Services
    .AddPayFastPayments(builder.Configuration)
    .AddStripePayments(builder.Configuration)
    .AddBhenguRedisCache(builder.Configuration); // <- registers Redis cache, replaces in-memory default
```

That single line swaps every provider's idempotency cache, Stripe Marketplace split-definition
store, Flutterwave plan store, Yoco token cache, MercadoPago / PagSeguro preapproval store, etc.
over to Redis. No provider code changes required.

## Configuration

| Key                | Default            | Notes |
|--------------------|--------------------|-------|
| `ConnectionString` | `localhost:6379`   | StackExchange.Redis connection string (cluster / sentinel / TLS supported via standard options). |
| `KeyPrefix`        | `bhengu:payments:` | Prefix applied to every key — change to share a Redis instance with other tenants safely. |

## Notes

- Multi-targets `net8.0` and `net10.0`.
- Uses `StackExchange.Redis` 2.8.16 (latest stable).
- JSON-serialises values with `System.Text.Json` so the wire format matches the in-memory cache.
- Key prefix is applied at the cache layer; consumers see the unprefixed key.
- `IConnectionMultiplexer` is registered as a singleton — share a single multiplexer per process
  (the official guidance from StackExchange.Redis).

## License

Apache 2.0 — same as the rest of the Bhengu Payments SDK.
