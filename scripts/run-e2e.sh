#!/bin/sh
# End-to-end live test of the GMO Coin plugin through the full Lean engine,
# using the official quantconnect/lean Docker image (no Lean fork needed).
#
# PLACES ONE REAL ORDER: minimum lot (0.00001 BTC, ~100 JPY exposure),
# post-only at -10% of market, canceled automatically after 90 seconds.
# Requires: Docker, .NET SDK 10, 1Password CLI, and >= ~150 JPY on the account.
#
# Usage: scripts/run-e2e.sh   (macOS / Linux)
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"

if [ -z "${GMOCOIN_API_KEY:-}" ] || [ -z "${GMOCOIN_API_SECRET:-}" ]; then
    # re-exec under op run so the 1Password references resolve into env vars
    echo "Resolving API keys from 1Password (op run)..."
    exec op run --env-file="$HERE/QuantConnect.GMOCoinBrokerage/.env.1password" -- "$0" "$@"
fi

echo "== 1/3 build plugin =="
dotnet build "$HERE/QuantConnect.GMOCoinBrokerage" -v q

echo "== 2/3 merge gmocoin rows into the image's symbol-properties / market-hours =="
BASE="${TMPDIR:-/tmp}/lean-data"
mkdir -p "$BASE/symbol-properties" "$BASE/market-hours"
docker run --rm -v "$HERE:/repo:ro" -v "$BASE:/out" --entrypoint /bin/sh quantconnect/lean:latest -c \
  'cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ &&
   cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ &&
   /repo/scripts/install-gmocoin-data.sh /out'

echo "== 3/3 run the E2E algorithm (REAL order; Ctrl+C to abort) =="
echo "Expected sequence: OnData ticks -> Submitted -> (90s) -> Canceled -> 'E2E: SUCCESS' -> auto exit"
docker run --rm -e GMOCOIN_API_KEY -e GMOCOIN_API_SECRET \
  -v "$HERE:/repo:ro" \
  -v "$BASE/symbol-properties/symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro" \
  -v "$BASE/market-hours/market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro" \
  --entrypoint /bin/sh quantconnect/lean:latest /repo/scripts/e2e-container.sh
