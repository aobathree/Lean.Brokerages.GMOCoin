# Lean.Brokerages.GMOCoin

GMO コイン(暗号通貨)の LEAN ブローカレッジプラグイン。設計は `docs/DESIGN.md`、
セットアップは `docs/SETUP.md`、AWS 運用は `docs/AWS.md`。

## 大原則: LEAN 本体をフォークしない

`Lean.Brokerages.Bitbank` / `Lean.Brokerages.kabuSTATION` と同一構成の姉妹プラグイン。
公式 `quantconnect/lean:latest` に DLL を足すだけで動く形を維持する。市場は
`GMOCoinMarket.cs` の `[ModuleInitializer]` が自己登録する。

## market id は 46(45 から移動済み)

2026-08-14 に **45 → 46** へ変更した(コミット `1ffa340`)。kabuSTATION も 45 を
使っており、`Market.Add` は**識別子の重複も `ArgumentException` にする**ため、
両プラグインを同一プロセス(＝同一統合イメージ)に載せた瞬間にクラッシュする状態だった。

現在の割り当て:

| プラグイン | market 名 | id |
|---|---|---|
| Bitbank | `bitbank` | 44 |
| kabuSTATION | `kabustation` | 45 |
| **GMOCoin** | `gmocoin` | **46** |
| MetaTrader5 | (独自市場なし。`Market.Oanda` を再利用) | — |

新規プラグインは 47 以降。upstream LEAN の `HardcodedMarkets` は KRX = 43 までなので、
追随のたびに末尾を確認して 44〜46 が取られていないか見ること。

id を変えても**ディスク上のデータは無傷**。パスは市場名ベース
(`Data/crypto/gmocoin/...`)で、id は `SecurityIdentifier` のバイナリ表現にしか入らない。
ただし**バックテスト成果物が既にある状態で id を変えると保存済み SID が解決できなくなる**。
移動が無害だったのは、まだワークスペースもイメージも無かったため。

## まだイメージに入っていない

`lean-jp:cli` 系の統合イメージには Bitbank と kabuSTATION しか入っていない。
GMOCoin を足すのは id 衝突を解消した今なら可能。手順は kabuSTATION の
`deploy/lean-cli/Dockerfile.cli` と同じく `--build-arg BASE_IMAGE=` で積む。

lean CLI ワークスペースもこの PC に未作成。

## リポジトリ運用

- 秘密情報は 1Password。`op run --env-file=<dir>/.env.1password -- <command>` で注入し、
  コミット対象は sample のみ
- 対応銘柄は取引所現物の 17 銘柄(`_JPY` サフィックス付きレバレッジ銘柄は対象外)。
  数値仕様は API を正とする(`tickSize` → `minimum_price_variation` 等)
