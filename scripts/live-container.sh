#!/bin/sh
# Runs INSIDE the quantconnect/lean container: installs the plugin DLL, patches
# config.json with the live-gmocoin environment and starts the engine with the
# configured algorithm. Used by scripts/run-live.* locally and by the systemd
# unit on AWS (deploy/aws/). Credentials arrive via the GMOCOIN_API_KEY /
# GMOCOIN_API_SECRET container environment variables.
#
# Optional overrides (container env):
#   ALGORITHM_TYPE_NAME  default GMOCoinMomentumRotation
#   ALGORITHM_LOCATION   default /repo/examples/gmocoin_momentum_rotation.py
set -eu

ALGORITHM_TYPE_NAME="${ALGORITHM_TYPE_NAME:-GMOCoinMomentumRotation}"
ALGORITHM_LOCATION="${ALGORITHM_LOCATION:-/repo/examples/gmocoin_momentum_rotation.py}"
export ALGORITHM_TYPE_NAME ALGORITHM_LOCATION

cp /repo/QuantConnect.GMOCoinBrokerage/bin/Debug/net10.0/QuantConnect.GMOCoinBrokerage.dll /Lean/Launcher/bin/Debug/
cd /Lean/Launcher/bin/Debug

python3 - <<'PYEOF'
import json
import os

def strip_jsonc(text):
    # Lean's config.json is JSONC (// and /* */ comments); strip them without
    # touching string contents (e.g. "https://..." urls)
    out = []
    i, n = 0, len(text)
    in_string = False
    while i < n:
        ch = text[i]
        if in_string:
            out.append(ch)
            if ch == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 2
                continue
            if ch == '"':
                in_string = False
            i += 1
        elif ch == '"':
            in_string = True
            out.append(ch)
            i += 1
        elif ch == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
        elif ch == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i += 2
        else:
            out.append(ch)
            i += 1
    return "".join(out)

path = "config.json"
config = json.loads(strip_jsonc(open(path).read()))
config.update({
    "environment": "live-gmocoin",
    "algorithm-type-name": os.environ["ALGORITHM_TYPE_NAME"],
    "algorithm-language": "Python",
    "algorithm-location": os.environ["ALGORITHM_LOCATION"],
})
config.setdefault("environments", {})["live-gmocoin"] = {
    "live-mode": True,
    "live-mode-brokerage": "GMOCoinBrokerage",
    "data-queue-handler": ["GMOCoinBrokerage"],
    "setup-handler": "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler",
    "result-handler": "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler",
    "data-feed-handler": "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed",
    "real-time-handler": "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler",
    "transaction-handler": "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler",
    "history-provider": ["BrokerageHistoryProvider", "SubscriptionDataReaderHistoryProvider"],
}
json.dump(config, open(path, "w"), indent=2)
print("config.json patched: %s (%s)" % (os.environ["ALGORITHM_TYPE_NAME"], os.environ["ALGORITHM_LOCATION"]))
PYEOF

exec dotnet QuantConnect.Lean.Launcher.dll
