# Lean.Brokerages.GMOCoin

[QuantConnect LEAN](https://github.com/QuantConnect/Lean) から日本の暗号資産取引所 [GMOコイン](https://coin.z.com) に接続する、**スタンドアロンのブローカレッジプラグイン**。

LEAN 本体の改変・再ビルドは不要。公式 NuGet パッケージ / 公式 Docker イメージ(`quantconnect/lean`)の上に、この DLL とデータ行を足すだけで動きます。macOS / Windows / Linux いずれでもビルド・実行できます([Lean.Brokerages.Bitbank](https://github.com/aobathree/Lean.Brokerages.Bitbank) の姉妹プラグイン)。

## 機能

- **ライブトレーディング**: 取引所現物(spot)の成行・指値・逆指値注文、post-only(SOK)、指値の価格変更(changeOrder)、残高同期、JPY 口座通貨
- **ライブデータフィード**: 約定(trades)・板スナップショット(orderbooks)のリアルタイム購読(`IDataQueueHandler`)
- **ヒストリカルデータ**: KLine API 経由の履歴取得(1min / 1hour / 1day)
- **対応銘柄**: 取引所現物の JPY 建て 17 銘柄(BTC, ETH, BCH, LTC, XRP, XLM, DOT, ATOM, FCR, ADA, LINK, DOGE, SOL, ASTR, NAC, SUI, WILD)
- 依存パッケージゼロ(Public / Private WebSocket とも Lean 標準の WebSocket ラッパー上に自前実装)

実装の設計判断・アーキテクチャは [docs/DESIGN.md](docs/DESIGN.md)、API キー設定は [docs/SETUP.md](docs/SETUP.md) を参照。

## 必要環境

- .NET SDK 10(DLL のビルドに使用。`dotnet --list-sdks` で 10.x があるか確認。無ければ macOS は `brew install dotnet-sdk`、Windows は `winget install Microsoft.DotNet.SDK.10`)
- Docker(公式 LEAN イメージでのバックテスト/ライブ実行に使用)
- GMOコインの API キー(ライブのみ。バックテストは不要)

## クイックスタート(公式 LEAN イメージでバックテスト)

bash(macOS / Linux / Git Bash)の場合。**Windows PowerShell の場合は[次節](#クイックスタートwindows-powershell)へ。**

```bash
git clone https://github.com/aobathree/Lean.Brokerages.GMOCoin.git
cd Lean.Brokerages.GMOCoin

# 1) プラグイン DLL をビルド(LEAN 本体はビルドしない。公式 NuGet 参照のみ)
dotnet build QuantConnect.GMOCoinBrokerage

# 2) 日足データを取得(公開 API、キー不要。Data/crypto/gmocoin/daily/ に保存)
dotnet run --project QuantConnect.GMOCoinBrokerage/tools/CandleDownloader -- --from 2018

# 3) 公式イメージの symbol-properties / market-hours に gmocoin 行をマージ
docker pull quantconnect/lean:latest
mkdir -p /tmp/lean-data/symbol-properties /tmp/lean-data/market-hours
C=$(docker create quantconnect/lean:latest)
docker cp $C:/Lean/Data/symbol-properties/symbol-properties-database.csv /tmp/lean-data/symbol-properties/
docker cp $C:/Lean/Data/market-hours/market-hours-database.json /tmp/lean-data/market-hours/
docker rm $C
scripts/install-gmocoin-data.sh /tmp/lean-data

# 4) サンプルアルゴリズム(Python、BTC/JPY のゴールデンクロス)をバックテスト
docker run --rm \
  -v $PWD/QuantConnect.GMOCoinBrokerage/bin/Debug/net10.0:/plugin:ro \
  -v /tmp/lean-data/symbol-properties/symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro \
  -v /tmp/lean-data/market-hours/market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro \
  -v $PWD/Data/crypto/gmocoin:/Lean/Data/crypto/gmocoin:ro \
  -v $PWD/examples:/Algo:ro \
  --entrypoint /bin/sh quantconnect/lean:latest -c \
  'cp /plugin/QuantConnect.GMOCoinBrokerage.dll /Lean/Launcher/bin/Debug/ &&
   cd /Lean/Launcher/bin/Debug &&
   dotnet QuantConnect.Lean.Launcher.dll \
     --algorithm-type-name GMOCoinSmaCrossExample \
     --algorithm-language Python \
     --algorithm-location /Algo/gmocoin_sma_cross.py'
```

最後に `STATISTICS::` ブロック(Total Orders / Net Profit / Total Fees ¥...)が出れば成功です。

## クイックスタート(Windows PowerShell)

ステップ 1)〜2)(`dotnet build` / `dotnet run`)は上と同じです。ステップ 3)〜4)を以下に読み替えます。マージスクリプト(POSIX sh + python3)は LEAN コンテナ内で実行するため、Git Bash や Python のホスト側インストールは不要です。

```powershell
# 3) 公式イメージの symbol-properties / market-hours に gmocoin 行をマージ
#    (抽出とマージをコンテナ内でまとめて実行)
docker pull quantconnect/lean:latest
$base = "$env:TEMP\lean-data"
New-Item -ItemType Directory -Force "$base\symbol-properties", "$base\market-hours" | Out-Null
docker run --rm -v "${PWD}:/repo:ro" -v "${base}:/out" --entrypoint /bin/sh quantconnect/lean:latest -c 'cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ && cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ && /repo/scripts/install-gmocoin-data.sh /out'

# 4) サンプルアルゴリズム(Python、BTC/JPY のゴールデンクロス)をバックテスト
docker run --rm `
  -v "${PWD}\QuantConnect.GMOCoinBrokerage\bin\Debug\net10.0:/plugin:ro" `
  -v "${base}\symbol-properties\symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro" `
  -v "${base}\market-hours\market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro" `
  -v "${PWD}\Data\crypto\gmocoin:/Lean/Data/crypto/gmocoin:ro" `
  -v "${PWD}\examples:/Algo:ro" `
  --entrypoint /bin/sh quantconnect/lean:latest -c 'cp /plugin/QuantConnect.GMOCoinBrokerage.dll /Lean/Launcher/bin/Debug/ && cd /Lean/Launcher/bin/Debug && dotnet QuantConnect.Lean.Launcher.dll --algorithm-type-name GMOCoinSmaCrossExample --algorithm-language Python --algorithm-location /Algo/gmocoin_sma_cross.py'
```

注意: `scripts/install-gmocoin-data.sh` は LF 改行必須です(`.gitattributes` で強制済み)。`/bin/sh: not found` 系のエラーが出る場合は `git checkout -- scripts/` で改行を正規化してください。

## アルゴリズムからの使い方

Python:

```python
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.GMOCoinBrokerage")
from QuantConnect.Brokerages.GMOCoin import GMOCoinBrokerageModel

class MyAlgorithm(QCAlgorithm):
    def initialize(self):
        self.set_account_currency("JPY")
        self.set_cash(1_000_000)
        self.set_brokerage_model(GMOCoinBrokerageModel())   # gmocoin 市場の登録も兼ねる
        self.btc = self.add_crypto("BTCJPY", Resolution.DAILY, "gmocoin").symbol
```

C# も同様に `SetBrokerageModel(new GMOCoinBrokerageModel())`(`using QuantConnect.Brokerages.GMOCoin;`)。

- 市場登録は DLL ロード時に自動で行われます(`Market.Add("gmocoin", 45)` 相当。id は config `gmocoin-market-id` で変更可)
- post-only 指値は `GMOCoinOrderProperties { PostOnly = true }` を注文プロパティに指定(GMOコインの `timeInForce: SOK`)
- 注文の変更は**価格のみ**可能(`changeOrder` API)。数量を変える場合は cancel + 再発注
- 対応注文タイプ: Market / Limit / StopMarket(GMO の逆指値 `STOP`)。StopLimit は非対応

## ライブトレーディング

config.json の `environments` に追加:

```jsonc
"live-gmocoin": {
  "live-mode": true,
  "live-mode-brokerage": "GMOCoinBrokerage",
  "data-queue-handler": [ "GMOCoinBrokerage" ],
  "setup-handler": "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler",
  "result-handler": "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler",
  "data-feed-handler": "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed",
  "real-time-handler": "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler",
  "transaction-handler": "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler",
  "history-provider": [ "BrokerageHistoryProvider", "SubscriptionDataReaderHistoryProvider" ]
}
```

API キーは環境変数 `GMOCOIN_API_KEY` / `GMOCOIN_API_SECRET` で注入します(config の `gmocoin-api-key` / `gmocoin-api-secret` でも可だが、平文保存は非推奨)。推奨は **1Password + `op` CLI**:

1Password に保存済みのキーがある場合は、テンプレート [QuantConnect.GMOCoinBrokerage/env.1password.sample](QuantConnect.GMOCoinBrokerage/env.1password.sample) を `.env.1password` にコピーし、op:// 秘密参照を自分のアイテムに書き換えるだけ(op:// 参照はボールト名・アイテム名という環境固有メタデータを含むため、実ファイルは git 管理外):

```powershell
Copy-Item QuantConnect.GMOCoinBrokerage/env.1password.sample QuantConnect.GMOCoinBrokerage/.env.1password
```

```bash
GMOCOIN_API_KEY="op://<vault>/<item>/username"
GMOCOIN_API_SECRET="op://<vault>/<item>/credential"
```

以降は op-run ラッパー経由で実行(キーは実行時に解決され、子プロセスの環境変数としてのみ注入)。Windows は cmd / PowerShell どちらからでも可、ExecutionPolicy の変更は不要:

```powershell
scripts\op-run.cmd dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
```

macOS / Linux は `scripts/op-run.sh` に読み替え。アイテムを新規に作る場合は `scripts\setup-1password.cmd`(macOS / Linux: `scripts/setup-1password.sh`)が対話作成〜疎通確認まで行います。

キー発行の権限設定(出金権限は必ず無効に)・AWS SSM を使った本番運用は [docs/SETUP.md](docs/SETUP.md) 参照。

ライブ前の疎通確認ツール(すべて `QuantConnect.GMOCoinBrokerage/tools/`):

| ツール | 内容 | 実注文 |
|---|---|---|
| `AssetsCheck` | 認証・残高・アクティブ注文・WSトークンの取得 | なし |
| `StreamCheck` | プライベート WebSocket(orderEvents / executionEvents)購読テスト | なし |
| `OrderSmokeTest` | 最小ロット指値の発注→取消ライフサイクル(`--yes` 必須) | **あり**(約定しない価格) |
| `CandleDownloader` | KLine の一括取得(Lean データ形式) | なし |

### Lean 本体 E2E テスト

エンジン全体(購読 → 発注 → 取消 → イベント反映)を公式 Docker イメージで通しで検証する。**実注文を 1 件発注する**(最小ロット 0.00001 BTC ≒ 100 円、市場価格の 90% + post-only で約定不可、90 秒後に自動取消。口座に 150 円以上必要):

```powershell
scripts\run-e2e.cmd
```

(macOS / Linux: `scripts/run-e2e.sh`。Docker Desktop を起動しておくこと)

内部で「プラグインのビルド → イメージの symbol-properties / market-hours への gmocoin 行マージ → config.json への `live-gmocoin` 環境パッチ → [examples/gmocoin_e2e_live.py](examples/gmocoin_e2e_live.py) の起動」まで自動実行する。合格条件: ログに `E2E OnData` のハートビート、`OnOrderEvent` の Submitted → Canceled 遷移、最後に `E2E: SUCCESS` が出て自動終了すること(タイムアウト 300 秒)。

## 長時間ライブ運用(モーメンタムローテーション + AWS)

長時間稼働用のサンプル戦略 [examples/gmocoin_momentum_rotation.py](examples/gmocoin_momentum_rotation.py) を同梱(日次モーメンタム上位 2 銘柄を保有、負モメンタム時は JPY 退避。キャッシュ口座向けに「売り約定確認 → 買い」の二段階リバランス、再起動安全)。

```powershell
# バックテスト(パラメータ調整用。要: CandleDownloader で 8 銘柄の日足取得)
# README のクイックスタート手順で --algorithm-type-name GMOCoinMomentumRotation を指定

# ローカルでライブ実行(実資金、Ctrl+C まで動き続ける)
scripts\run-live.cmd
```

AWS(EC2 + systemd + SSM Parameter Store)での 24/7 稼働手順は **[docs/AWS.md](docs/AWS.md)** を参照。`deploy/aws/setup-ec2.sh` がインスタンス上のセットアップ(Docker・プラグインビルド・データマージ・systemd 登録)を一括で行います。

## テスト

```bash
dotnet test QuantConnect.GMOCoinBrokerage.Tests   # 24 tests、ネットワーク不要
```

## 制限事項(v1)

- 取引所現物のみ(レバレッジ取引 API・販売所は未対応)
- Second / Tick 解像度の履歴・Quote 履歴は非対応(ライブの板購読は対応)
- 分足・時間足の履歴は 2021-04-15 以降のみ(KLine API の提供開始日)
- GMOコインにはテストネットが無いため、ライブ検証は本番口座 + 最小ロットで行うこと

## ライセンス

Apache License 2.0(LEAN 本体と同じ)。本ソフトウェアの利用による損失について作者は責任を負いません。自動売買は自己責任で、必ず少額から検証してください。
