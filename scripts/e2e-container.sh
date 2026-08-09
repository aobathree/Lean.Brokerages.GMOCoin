#!/bin/sh
# Runs INSIDE the quantconnect/lean container (started by run-e2e.ps1/.sh):
# installs the plugin DLL next to the Launcher, patches config.json with the
# live-gmocoin environment and starts the engine. Credentials arrive via the
# GMOCOIN_API_KEY / GMOCOIN_API_SECRET container environment variables.
set -eu

cp /repo/QuantConnect.GMOCoinBrokerage/bin/Debug/net10.0/QuantConnect.GMOCoinBrokerage.dll /Lean/Launcher/bin/Debug/
cd /Lean/Launcher/bin/Debug

python3 - <<'PYEOF'
import json

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
    "algorithm-type-name": "GMOCoinE2ETestAlgorithm",
    "algorithm-language": "Python",
    "algorithm-location": "/repo/examples/gmocoin_e2e_live.py",
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
print("config.json patched for the live-gmocoin E2E run")
PYEOF

exec dotnet QuantConnect.Lean.Launcher.dll
