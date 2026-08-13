# Lean × GMOコイン コネクター設計文書

**Status:** v1.0(2026-08-09)
**成果物:** `QuantConnect.GMOCoinBrokerage`(スタンドアロンのブローカレッジプラグイン)
**参照実装:** [Lean.Brokerages.Bitbank](https://github.com/aobathree/Lean.Brokerages.Bitbank)(同一アーキテクチャの姉妹プラグイン)

---

## 1. 目的とスコープ

QuantConnect の Lean エンジンから、日本の暗号資産取引所 **GMOコイン**(coin.z.com)に接続し、以下を可能にする。

- **ライブトレーディング**: 取引所現物(spot)の成行・指値・逆指値注文の発注/変更/取消、残高の同期
- **ライブデータフィード**: 約定・板情報のリアルタイム購読(`IDataQueueHandler`)
- **ヒストリカルデータ**: KLine API 経由の履歴取得(`GetHistory` / `BrokerageHistoryProvider`)

### スコープ外(v1)

- レバレッジ取引(`BTC_JPY` 等の `_JPY` サフィックス銘柄、建玉・決済注文 API)
- 販売所(取引所現物のみ)
- 入出金・振替操作

---

## 2. アーキテクチャ

QuantConnect 公式のブローカレッジ構成([Lean.Brokerages.Template](https://github.com/QuantConnect/Lean.Brokerages.Template))に倣い、**Lean 本体とは別のプラグインパッケージ**として実装。Lean の `Composer` が `IBrokerageFactory` / `IDataQueueHandler` 実装を DLL スキャンで自動発見するため、ビルド成果物を Launcher の出力ディレクトリに置き、config.json で型名を指定するだけで組み込める。

```
┌─────────────────────────── Lean Engine ───────────────────────────┐
│  BrokerageTransactionHandler      LiveTradingDataFeed             │
│        │ PlaceOrder/Cancel              │ Subscribe               │
│        ▼                                ▼                         │
│  ┌──────────────────────────────────────────────────────┐         │
│  │   GMOCoinBrokerage : Brokerage, IDataQueueHandler    │         │
│  └──────┬──────────────────┬──────────────────┬─────────┘         │
└─────────┼──────────────────┼──────────────────┼───────────────────┘
          │ REST (HMAC)      │ Private WS       │ Public WS
          ▼                  ▼                  ▼
 api.coin.z.com/private  /ws/private/v1     /ws/public/v1
 (注文・残高)+ /public   /{accessToken}     (trades,
 (KLine・取引ルール)     (orderEvents,       orderbooks)
                          executionEvents)
```

GMOコインは 3 つのトランスポートを使う。bitbank 版との対比:

| 用途 | GMOコイン | (bitbank 版) |
|---|---|---|
| 注文・残高・履歴 | REST(HMAC-SHA256 署名)`https://api.coin.z.com/private` + `/public` | 同方式 |
| 注文イベント・約定 | **Private WebSocket**(`POST /v1/ws-auth` のトークンを URL に埋め込む) | PubNub ロングポーリング |
| 市場データ | **Public WebSocket**(素の WS + JSON subscribe コマンド) | Socket.IO 4 |

いずれも Lean 標準の `WebSocketClientWrapper` の上に必要最小プロトコルを自前実装しており、**外部依存パッケージはゼロ**。

### 2.1 認証(`GMOCoinAuthenticator`)

- ヘッダー: `API-KEY` / `API-TIMESTAMP`(UNIX ms)/ `API-SIGN`
- 署名 = HMAC-SHA256(hex)。メッセージは `{timestamp}{METHOD}{path}{body}`
  - path は `/v1` 始まり(`/private` を含めない)、**クエリ文字列は含めない**
  - GET の body は空文字列
- レスポンスは常に `{"status": 0, "data": ..., "responsetime": ...}` エンベロープ。エラー時は `messages[].message_code`(ERR-XXXX)を `GMOCoinApiException` にマップ

### 2.2 レートリミッター(Lean の `RateGate`)

Private API は先週の取引高による Tier 制(Tier 1: GET/POST とも 20 req/s)。コネクターは安全側の GET 10 req/s、POST/PUT 6 req/s、Public 10 req/s で制限。WebSocket の subscribe/unsubscribe は **1 コマンド/秒**(仕様上の上限)で送信キューをスロットリングする。

### 2.3 プライベートストリーム(`GMOCoinPrivateWebSocketClient`)

1. 署名付き `POST /v1/ws-auth` → アクセストークン(TTL 60 分)
2. `wss://api.coin.z.com/ws/private/v1/{token}` に接続し、`executionEvents` / `orderEvents` を subscribe(1 秒間隔)
3. **トークン延長**: 25 分ごとに `PUT /v1/ws-auth`(延長後の TTL は常に 60 分)。延長失敗時は新規トークンを取得して再接続
4. 切断時は `WebSocketClientWrapper` が自動再接続し、Open イベントで再 subscribe

メッセージと Lean イベントへのマッピング:

| GMOコイン メッセージ | Lean 側の処理 |
|---|---|
| `orderEvents` msgType=NOR(ORDERED/WAITING) | 無視(発注時に Submitted 済み) |
| `orderEvents` msgType=ROR | `OrderStatus.Invalid`(拒否) |
| `orderEvents` orderStatus=CANCELED | `OrderStatus.Canceled` |
| `orderEvents` orderStatus=EXPIRED(EXPIRED_SOK 等) | `OrderStatus.Canceled` + cancelType をメッセージに付記 |
| `executionEvents`(msgType=ER) | 約定。`orderExecutedSize >= orderSize` なら Filled、未満なら PartiallyFilled。`fee`(JPY、Taker 正 / Maker 負)を `OrderEvent.OrderFee` に反映 |

約定数量の集計は `executionEvents.orderExecutedSize`(サーバー側の累計)を正とするため、クライアント側での約定量の積算は不要。

### 2.4 注文イベントの競合対策

REST の発注応答より先に WebSocket の約定通知が届くレースに備え、Lean 標準の `BrokerageConcurrentMessageHandler<T>` を使用。WS コールバックは `HandleNewMessage(msg)` に流し、`PlaceOrder` / `UpdateOrder` / `CancelOrder` の REST 呼び出しは `WithLockedStream(...)` で包む。これにより `BrokerId` 未設定の状態で約定イベントを処理してしまう事故を防ぐ。

### 2.5 マーケットデータ(`GMOCoinPublicWebSocketClient` + IDataQueueHandler)

- `EventBasedDataQueueHandlerSubscriptionManager` で `(Symbol, TickType)` の購読を参照カウント管理(tick type ごとに独立チャネル)

| Lean TickType | GMOコイン チャネル | 生成する BaseData |
|---|---|---|
| Trade | `trades` | `Tick(TickType.Trade)`(price, size) |
| Quote | `orderbooks` | スナップショットの best bid/ask から `Tick(TickType.Quote)` |

- `orderbooks` は**フルスナップショット配信**のため、bitbank のような差分適用・シーケンス管理は不要(best bid = `bids[0]`、best ask = `asks[0]`)
- ティックは `IDataAggregator` の `Update()` に流し、Lean 側で任意の Resolution に集約
- subscribe コマンドは 1/秒制限があるため、専用送信キューでスロットリング。再接続時は追跡中の全チャネルを再 subscribe

### 2.6 タイムスタンプの扱い

GMOコインのタイムスタンプは ISO-8601 文字列(`2019-03-19T02:15:06.081Z`)。Json.NET の既定(`DateParseHandling.DateTime`)で `JObject.Parse` すると日付風文字列は DateTime トークン化されるため、DTO のタイムスタンプは **`DateTime` 型で受ける**(string で受けるとミリ秒が失われる)。`GMOCoinTime.ToUtc` が Kind を UTC に正規化する。

---

## 3. シンボルマッピング

**カスタムマッパー不要**。`SymbolPropertiesDatabaseSymbolMapper("gmocoin")` が symbol-properties DB の `market_ticker` 列(GMO の銘柄コード、例 `BTC`)で双方向マッピングする。

- Lean シンボル: `Symbol.Create("BTCJPY", SecurityType.Crypto, "gmocoin")`
- **対応銘柄(v1): 取引所現物の 17 銘柄**(2026-08-09 に `GET /public/v1/symbols` から取得):
  `BTC, ETH, BCH, LTC, XRP, XLM, DOT, ATOM, FCR, ADA, LINK, DOGE, SOL, ASTR, NAC, SUI, WILD`
- `_JPY` サフィックス付き(レバレッジ)銘柄は対象外
- 数値仕様は API を正とする: `tickSize` → `minimum_price_variation`、`sizeStep` → `lot_size`、`minOrderSize` → `minimum_order_size`
- 市場 id は 46(bitbank プラグインの 44、kabuSTATION プラグインの 45 と併用可能。config `gmocoin-market-id` で変更可)

## 4. 口座通貨 = JPY

- `Brokerage.AccountBaseCurrency = Currencies.JPY`
- symbol-properties に全 JPY 建てペアを揃えることで、CashBook の JPY 換算が解決可能
- 手数料は常に JPY(quote)建て。`executionEvents.fee` は Taker が正、Maker が負(リベート)

## 5. GMOCoinBrokerageModel / FeeModel

- 対応注文タイプ: `Market, Limit, StopMarket`(GMO の `MARKET / LIMIT / STOP`)。StopLimit は API に存在しないため非対応
- `CanUpdateOrder`: **価格のみ変更可**(`POST /v1/changeOrder`)。数量変更は false(cancel-replace を強制)。bitbank(amend 全面不可)との相違点
- timeInForce: Lean の GTC のみ対応。指値の既定は FAS(残数量有効)、post-only は `SOK`
- FeeModel 既定値: **maker -0.03% / taker 0.09%**(標準料率。BTC/ETH/XRP は 2026-08 時点 -0.01% / 0.05%)。コンストラクタで注入可能。実料率は `GET /public/v1/symbols` の `makerFee` / `takerFee`、ライブの確定値は `executionEvents.fee`

## 6. ヒストリカルデータ(GetHistory)

`GET /public/v1/klines?symbol=&interval=&date=` を使用。

| Lean Resolution | interval | date パラメータ |
|---|---|---|
| Minute | `1min` | `YYYYMMDD`(**朝 6:00 JST 区切りの取引日**。2021-04-15 以降のみ) |
| Hour | `1hour` | 同上 |
| Daily | `1day` | `YYYY`(年単位) |

- レスポンスの `openTime`(UNIX ms)から `TradeBar` を生成
- 日足の openTime は 6:00 JST(= 前日 21:00 UTC)である点に注意
- Second / Tick 解像度、`TickType.Quote` の履歴は非対応(null を返し警告)

## 7. エラーハンドリング

主要な ERR コードのマッピング(`GMOCoinRestApiClient.GetErrorDescription`):

| コード | 意味 | Lean 側の扱い |
|---|---|---|
| ERR-5008/5009/5010/5011/5012 | 時刻ずれ・署名・認証エラー | `BrokerageMessageEvent` 警告 |
| ERR-5003 | レート制限超過 | RateGate で予防。発生時は例外 → 警告 |
| ERR-201 / ERR-208 | 残高・保有数量不足 | `OrderStatus.Invalid` |
| ERR-5122 | 注文が既に取消済/約定済等 | **Cancel 時は成功扱いに正規化**(冪等性) |
| ERR-5126 / ERR-5114 | 数量の上限/下限/刻み違反 | `OrderStatus.Invalid` |
| ERR-5129 | 逆指値が即約定する価格 | `OrderStatus.Invalid` |
| ERR-5201/5202/5203 | メンテナンス・プレオープン | 警告(発注は例外 → Invalid) |
| ERR-5206 | 注文変更回数の上限 | UpdateOrder 失敗 → 警告(cancel + 再発注を促す) |
| ERR-5207 | KLine の日付範囲外 | 空リストに正規化(履歴のページング継続) |

## 8. テスト

| レイヤー | 内容 |
|---|---|
| 単体(24 件、ネットワーク不要) | 署名生成(独立計算した既知ベクトル)、注文リクエスト構築、注文復元、エンベロープ解析、KLine 日付キー(6:00 JST 区切り・2021-04-15 クランプ)、WS コマンド構築、公式ドキュメントのサンプルメッセージのデシリアライズ |
| 結合(要 API キー) | `tools/AssetsCheck` → `tools/StreamCheck` → `tools/OrderSmokeTest`(最小ロット 0.00001 BTC) |
| E2E | `live-gmocoin` 環境 + 最小額アルゴリズムで Launcher を起動し、購読〜発注〜取消を確認 |

GMOコインにはテストネットが無いため、結合テスト以降は**本番口座 + 最小ロット**で行う。

## 9. 既知の注意点

- **指値の価格制限**: GMO は市場価格から離れすぎた指値を ERR-5121(Too low/high price)で拒否する(実機で確認: BTC 現物で最良気配の 50% は拒否、90% は受付)。bitbank のような「50% 下の安全指値」は使えないため、OrderSmokeTest は -10% + SOK(post-only)で約定不能を担保している
- `GET /v1/activeOrders` は symbol 必須のため、`GetOpenOrders()` は全 17 銘柄を順に照会する(レートゲート内で約 2 秒)
- 逆指値(STOP)注文は REST/WS の注文ステータスで `WAITING` が「有効」、`ORDERED` が「一部約定」を意味する(通常注文と逆に見えるので注意)
- Private WebSocket のアクセストークンは同時 5 個まで。超過すると古いものから削除されるため、複数プロセスで同一キーを使い回さない
- Public WebSocket はサーバーから 1 分ごとに ping が来る(3 回無応答で切断)。.NET の `ClientWebSocket` が自動で pong を返すため対応不要
- upstream の NuGet 監査警告(DotNetZip / System.Drawing.Common)は Lean 本体由来で、`Directory.Build.props` で該当 advisory のみ抑制済み(bitbank 版と同じ判断)
