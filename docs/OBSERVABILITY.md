# Observability

The SDK ships OpenTelemetry-native diagnostics. Wire them up once and every provider call shows up in your traces, metrics, and logs.

## Wire it up

```csharp
using Bhengu.Finance.Payments.Core.Observability;

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("my-checkout-service"))
    .WithTracing(b => b
        .AddSource(BhenguPaymentDiagnostics.ActivitySourceName)
        .AddHttpClientInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(b => b
        .AddMeter(BhenguPaymentDiagnostics.MeterName)
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());
```

## What you get

### Spans (Activities)

| Operation | Span name | Kind | Tags |
|---|---|---|---|
| Charge | `payment.charge` | Client | `payment.provider`, `payment.currency`, `payment.amount`, `payment.gateway_reference`, `payment.status` |
| Refund | `payment.refund` | Client | `payment.provider`, `payment.gateway_reference`, `payment.refund_amount`, `payment.status` |
| Payout | `payment.payout` | Client | `payment.provider`, `payment.currency`, `payment.amount`, `payment.status` |

Spans are emitted by providers that opt into instrumentation. Exception details land on the span's `Status` and `Events`.

### Metrics

| Metric | Unit | Tags | Use |
|---|---|---|---|
| `bhengu_payments_charges_total` | `{charge}` | `provider`, `outcome` | Charge volume / decline rate per provider |
| `bhengu_payments_refunds_total` | `{refund}` | `provider`, `outcome` | Refund pressure per provider |
| `bhengu_payments_payouts_total` | `{payout}` | `provider`, `outcome` | Payout cadence |
| `bhengu_payments_charge_duration_ms` | `ms` | `provider`, `outcome` | P50/P95/P99 latency per provider |
| `bhengu_payments_webhook_verifications_total` | `{verification}` | `provider`, `valid` | Tamper-attempt detection |

### Outcome tags

Standardised via `BhenguPaymentDiagnostics.Outcomes`:
- `success` — provider returned `PaymentStatus.Completed`
- `pending` — provider returned `PaymentStatus.Pending` (async settlement; webhook will follow)
- `declined` — provider declined (`PaymentDeclinedException`)
- `rate_limited` — provider rate-limited (`ProviderRateLimitException`)
- `unavailable` — provider unreachable (`ProviderUnavailableException`)
- `error` — uncategorised failure (`BhenguPaymentException` base)

## Logs

The SDK uses `ILogger<T>` with provider-name scopes. Recommended consumer config:

```csharp
builder.Logging.AddJsonConsole(opts => opts.IncludeScopes = true);
```

Each provider logs at:
- `Information` — outcome only ("charge succeeded", "refund processed")
- `Warning` — recoverable failures (missing webhook secret, parse degradation)
- `Error` — unexpected exceptions BEFORE rethrowing — your log aggregator sees them once with full stack

The SDK **never logs**:
- Card numbers, CVVs, PINs (it never sees them; client-side tokenisation only)
- API keys, passphrases, signing secrets
- Full payer PII (names, addresses) — only the gateway reference

## Sampling

For high-volume merchants, sample with a parent-based sampler:

```csharp
.WithTracing(b => b.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(0.05))))
```

5% sample of unsourced traffic; honour upstream span sampling decisions.

## Notes

The OpenTelemetry surface is stable and follows [semantic-conventions/payment](https://github.com/open-telemetry/semantic-conventions/tree/main/docs/payment) where applicable.

Provider instrumentation density varies — `Bhengu.Finance.Payments.Core` ships the `ActivitySource` and `Meter` so consumers can emit spans even when wrapping providers, even where individual providers haven't been individually instrumented yet.
