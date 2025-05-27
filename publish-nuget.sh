#!/bin/bash

set -e

echo "Building the solution..."
dotnet build Bhengu.Finance.Payments.sln -c Release

echo "Running tests..."
dotnet test Bhengu.Finance.Payments.Tests
dotnet test Bhengu.Finance.Payments.IntegrationTests

echo "Packing Core project..."
dotnet pack Bhengu.Finance.Payments.Core/Bhengu.Finance.Payments.Core.csproj -c Release -o ./nupkgs

echo "Use the following to publish:"
echo "dotnet nuget push ./nupkgs/*.nupkg --api-key <YOUR_KEY> --source https://api.nuget.org/v3/index.json"