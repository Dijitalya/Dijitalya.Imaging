$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
New-Item -ItemType Directory -Force 'artifacts/validation' | Out-Null
function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}
Invoke-Dotnet restore
Invoke-Dotnet build --no-restore --configuration Release
Invoke-Dotnet test --no-build --configuration Release --logger trx --results-directory artifacts/validation
Invoke-Dotnet run --project tools/Imaging.Validation --configuration Release -- --smoke
Invoke-Dotnet run --project tools/Imaging.Validation --configuration Release -- --benchmark 5 | Set-Content 'artifacts/validation/benchmark.csv'
Invoke-Dotnet pack src/Dijitalya.Imaging --no-build --configuration Release --output artifacts
