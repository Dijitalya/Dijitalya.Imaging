#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p artifacts/validation
dotnet restore
dotnet build --no-restore --configuration Release
dotnet test --no-build --configuration Release --logger trx --results-directory artifacts/validation
dotnet run --project tools/Imaging.Validation --configuration Release -- --smoke
dotnet run --project tools/Imaging.Validation --configuration Release -- --benchmark 5 > artifacts/validation/benchmark.csv
dotnet pack src/Dijitalya.Imaging --no-build --configuration Release --output artifacts
