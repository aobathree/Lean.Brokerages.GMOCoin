#!/bin/sh
# Runs the momentum rotation algorithm LIVE with REAL MONEY on the local
# machine (useful for validation before the AWS deployment; see docs/AWS.md).
# Runs until Ctrl+C. Requires Docker, .NET SDK 10 and the 1Password CLI.
#
# Usage:
#   scripts/run-live.sh                                             # momentum rotation (default)
#   scripts/run-live.sh GMOCoinAtrGrid24H /repo/examples/gmocoin_atr_grid_24h.py
set -eu
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ALGO_NAME="${1:-GMOCoinMomentumRotation}"
ALGO_FILE="${2:-/repo/examples/gmocoin_momentum_rotation.py}"

if [ -z "${GMOCOIN_API_KEY:-}" ] || [ -z "${GMOCOIN_API_SECRET:-}" ]; then
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

echo "== 3/3 run $ALGO_NAME LIVE (REAL MONEY; Ctrl+C to stop) =="
docker run --rm -e GMOCOIN_API_KEY -e GMOCOIN_API_SECRET \
  -e "ALGORITHM_TYPE_NAME=$ALGO_NAME" -e "ALGORITHM_LOCATION=$ALGO_FILE" \
  -v "$HERE:/repo:ro" \
  -v "$BASE/symbol-properties/symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro" \
  -v "$BASE/market-hours/market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro" \
  --entrypoint /bin/sh quantconnect/lean:latest /repo/scripts/live-container.sh
