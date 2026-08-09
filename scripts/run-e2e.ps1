# End-to-end live test of the GMO Coin plugin through the full Lean engine,
# using the official quantconnect/lean Docker image (no Lean fork needed).
#
# PLACES ONE REAL ORDER: minimum lot (0.00001 BTC, ~100 JPY exposure),
# post-only at -10% of market, canceled automatically after 90 seconds.
# Requires: Docker, .NET SDK 10, 1Password CLI, and >= ~150 JPY on the account.
#
# Usage: scripts\run-e2e.cmd   (from cmd.exe or PowerShell)
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $env:GMOCOIN_API_KEY -or -not $env:GMOCOIN_API_SECRET) {
    # re-exec under op run so the 1Password references resolve into env vars
    Write-Host "Resolving API keys from 1Password (op run)..."
    op run --env-file="$repoRoot\QuantConnect.GMOCoinBrokerage\.env.1password" -- `
        powershell -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
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

Write-Host "== 3/3 run the E2E algorithm (REAL order; Ctrl+C to abort) =="
Write-Host "Expected sequence: OnData ticks -> Submitted -> (90s) -> Canceled -> 'E2E: SUCCESS' -> auto exit"
docker run --rm -e GMOCOIN_API_KEY -e GMOCOIN_API_SECRET `
  -v "${repoRoot}:/repo:ro" `
  -v "${base}\symbol-properties\symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro" `
  -v "${base}\market-hours\market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro" `
  --entrypoint /bin/sh quantconnect/lean:latest /repo/scripts/e2e-container.sh
exit $LASTEXITCODE
