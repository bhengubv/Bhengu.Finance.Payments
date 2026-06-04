# Bhengu.Finance.Payments.Stitch

Stitch adapter for the Bhengu.Finance.Payments family — South African open-banking pay-by-bank, InstantEFT, LinkPay, and bank-to-bank payouts via the Stitch GraphQL API. Covers FNB, ABSA, Standard Bank, Nedbank, Capitec, Investec, and Discovery. Charge, refund, webhook verification, payouts, and recurring mandates behind the Bhengu canonical contracts.

## Install

```sh
dotnet add package Bhengu.Finance.Payments.Stitch
```

## What this package gives you

| Contract | Provider class | Notes |
|---|---|---|
| `IPaymentGatewayProvider` | `StitchPaymentProvider` | Pay-by-bank charge / refund / webhook verify |
| `IPayoutProvider` | `StitchPaymentProvider` | Bank-to-bank disbursement via GraphQL |
| `IMandateProvider` | `StitchMandateProvider` | Debit-order / pull-payment mandates |

## Wiring

```csharp
builder.Services.AddStitchPayments(builder.Configuration);
```

Bind options from `Bhengu:Finance:Payments:Stitch`:

```jsonc
{
  "Bhengu": {
    "Finance": {
      "Payments": {
        "Stitch": {
          "ClientId": "test-...",
          "ApiKey": "sk_test_...",
          "WebhookSecret": "...",
          "ClientAssertionJwt": null,              // alternative to ApiKey (full OAuth2)
          "BeneficiaryAccountNumber": "1234567890",
          "BeneficiaryBankId": "absa",
          "BeneficiaryName": "Acme Pty Ltd",
          "Currency": "ZAR",
          "UseSandbox": true,
          "BaseUrl": null,            // optional override
          "GraphqlEndpoint": null,    // optional override
          "SandboxUrl": null,         // optional override
          "TokenEndpoint": null,      // optional override
          "ClientSecret": null        // optional, for OAuth2 client-credentials
        }
      }
    }
  }
}
```

## Usage

```csharp
[ApiController]
public class CheckoutController(
    [FromKeyedServices(ProviderNames.Stitch)] IPaymentGatewayProvider gateway) : ControllerBase
{
    [HttpPost("charge")]
    public async Task<PaymentResponse> Charge([FromBody] PaymentRequest request)
        => await gateway.ProcessPaymentAsync(request);
}
```

`PayoutRequest.DestinationToken` format: `"<bankId>:<accountNumber>:<beneficiaryName>"`
(e.g. `"capitec:1234567890:Jane Doe"`).

## Capabilities at runtime

```csharp
if (gateway.Capabilities.HasFlag(ProviderCapabilities.Refund))
    await gateway.ProcessRefundAsync(refundRequest);
```

## Status

- Apache-2.0
- Multi-target: net8.0 + net10.0
- Source: https://github.com/bhengubv/Bhengu.Finance.Payments

For full SDK docs, observability wiring, resilience configuration and the family map see
the [main README](https://github.com/bhengubv/Bhengu.Finance.Payments).
