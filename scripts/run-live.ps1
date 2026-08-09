# Runs the momentum rotation algorithm LIVE with REAL MONEY on the local
# machine (useful for validation before the AWS deployment; see docs/AWS.md).
# Runs until Ctrl+C. Requires Docker, .NET SDK 10 and the 1Password CLI.
#
# Usage:
#   scripts\run-live.cmd                                  # momentum rotation (default)
#   scripts\run-live.cmd -AlgorithmName GMOCoinAtrGrid24H -AlgorithmFile /repo/examples/gmocoin_atr_grid_24h.py
param(
    [string]$AlgorithmName = "GMOCoinMomentumRotation",
    [string]$AlgorithmFile = "/repo/examples/gmocoin_momentum_rotation.py"
)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $env:GMOCOIN_API_KEY -or -not $env:GMOCOIN_API_SECRET) {
    # re-exec under op run so the 1Password references resolve into env vars
    Write-Host "Resolving API keys from 1Password (op run)..."
    op run --env-file="$repoRoot\QuantConnect.GMOCoinBrokerage\.env.1password" -- `
        powershell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
        -AlgorithmName $AlgorithmName -AlgorithmFile $AlgorithmFile
    exit $LASTEXITCODE
}

Write-Host "== 1/3 build plugin =="
dotnet build "$repoRoot\QuantConnect.GMOCoinBrokerage" -v q
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== 2/3 merge gmocoin rows into the image's symbol-properties / market-hours =="
$base = Join-Path $env:TEMP "lean-data"
New-Item -ItemType Directory -Force "$base\symbol-properties" | Out-Null
New-Item -ItemType Directory -Force "$base\market-hours" | Out-Null
docker run --rm -v "${repoRoot}:/repo:ro" -v "${base}:/out" --entrypoint /bin/sh quantconnect/lean:latest -c 'cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ && cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ && /repo/scripts/install-gmocoin-data.sh /out'
if ($LASTEXITCODE -ne 0) { exit 1 }

Write-Host "== 3/3 run $AlgorithmName LIVE (REAL MONEY; Ctrl+C to stop) =="
docker run --rm -e GMOCOIN_API_KEY -e GMOCOIN_API_SECRET `
  -e "ALGORITHM_TYPE_NAME=$AlgorithmName" -e "ALGORITHM_LOCATION=$AlgorithmFile" `
  -v "${repoRoot}:/repo:ro" `
  -v "${base}\symbol-properties\symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro" `
  -v "${base}\market-hours\market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro" `
  --entrypoint /bin/sh quantconnect/lean:latest /repo/scripts/live-container.sh
exit $LASTEXITCODE
