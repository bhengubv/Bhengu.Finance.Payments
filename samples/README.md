# Samples

Runnable mini-apps showing how to integrate Bhengu.Finance.Payments.

| Sample | What it shows |
|---|---|
| [`BasicCharge`](BasicCharge/) | Multi-provider checkout API with keyed-services routing, webhook handler, OpenTelemetry, capability querying |

## Running

```sh
cd samples/BasicCharge
dotnet user-secrets set "Bhengu:Finance:Payments:PayFast:MerchantId" "10000100"
dotnet user-secrets set "Bhengu:Finance:Payments:PayFast:MerchantKey" "46f0cd694581a"
dotnet user-secrets set "Bhengu:Finance:Payments:PayFast:Passphrase" "jt7NOE43FZPn"
dotnet run
```

The samples deliberately reference the SDK by `ProjectReference`, not `PackageReference`. To run them against the published NuGets, switch the csproj.
