# Runs any command with GMOCOIN_API_KEY / GMOCOIN_API_SECRET resolved from
# 1Password at launch time (injected into the child process only, never
# written to disk).
#
# Usage (Windows PowerShell):
#   scripts\op-run.ps1 dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
#   scripts\op-run.ps1 dotnet QuantConnect.Lean.Launcher.dll --environment live-gmocoin
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $repoRoot "QuantConnect.GMOCoinBrokerage\.env.1password"
op run --env-file="$envFile" -- @args
exit $LASTEXITCODE
