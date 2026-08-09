#!/bin/sh
# Merges the GMO Coin entries into an existing Lean data folder:
#   - symbol-properties-database.csv : appends the 17 GMO Coin spot symbol rows
#   - market-hours-database.json     : adds the Crypto-gmocoin-[*] entry
# Idempotent: existing gmocoin entries are replaced, everything else untouched.
#
# Usage: scripts/install-gmocoin-data.sh /path/to/Lean/Data
set -eu
TARGET="${1:?usage: install-gmocoin-data.sh /path/to/Lean/Data}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"

CSV="$TARGET/symbol-properties/symbol-properties-database.csv"
MH="$TARGET/market-hours/market-hours-database.json"
[ -f "$CSV" ] || { echo "ERROR: $CSV not found (is $TARGET a Lean data folder?)"; exit 1; }
[ -f "$MH" ] || { echo "ERROR: $MH not found"; exit 1; }

# symbol properties: drop existing gmocoin rows, append ours
grep -v '^gmocoin,' "$CSV" > "$CSV.tmp"
tail -n +2 "$HERE/Data/symbol-properties/symbol-properties-database.csv" >> "$CSV.tmp"
mv "$CSV.tmp" "$CSV"
echo "symbol-properties: $(grep -c '^gmocoin,' "$CSV") gmocoin rows installed"

# market hours: merge the Crypto-gmocoin-[*] entry
python3 - "$MH" "$HERE/Data/market-hours/market-hours-database.json" <<'EOF'
import json, sys
target_path, source_path = sys.argv[1], sys.argv[2]
target = json.load(open(target_path))
source = json.load(open(source_path))
target["entries"].update(source["entries"])
json.dump(target, open(target_path, "w"), indent=2)
print("market-hours: entries installed:", ", ".join(source["entries"]))
EOF
