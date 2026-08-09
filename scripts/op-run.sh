#!/bin/sh
# Runs any command with GMOCOIN_API_KEY / GMOCOIN_API_SECRET resolved from
# 1Password at launch time (injected into the child process only, never
# written to disk).
#
# Usage:
#   scripts/op-run.sh dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
#   scripts/op-run.sh dotnet QuantConnect.Lean.Launcher.dll --environment live-gmocoin
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
exec op run --env-file="$HERE/QuantConnect.GMOCoinBrokerage/.env.1password" -- "$@"
