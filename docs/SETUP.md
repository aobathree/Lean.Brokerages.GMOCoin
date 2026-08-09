# GMOコイン API キー設定ガイド

Lean × GMOコイン コネクターの API キー設定手順。**キーの実値は 1Password(ローカル)/ AWS SSM パラメータストア(AWS)にのみ置き、ファイルや config.json には書かない**。

コネクターは次の優先順位で認証情報を解決する(`GMOCoinBrokerageFactory.GetCredential`):

1. `config.json` の `gmocoin-api-key` / `gmocoin-api-secret`(通常は空のまま)
2. 環境変数 `GMOCOIN_API_KEY` / `GMOCOIN_API_SECRET` ← **こちらを使う**

---

## 1. GMOコイン側: API キーの発行

1. [GMOコイン会員ページ](https://coin.z.com/member/) にログイン → **API** → **APIキーを発行**
2. パーミッションは次のとおり設定する(機能ごとにチェックボックスがある):

   | 権限 | 設定 | 理由 |
   |---|---|---|
   | 資産残高の参照 | ✅ 有効 | GetCashBalance に必要 |
   | 最新の注文・約定情報の参照 | ✅ 有効 | GetOpenOrders / 注文状態の最終確認に必要 |
   | 注文(発注・変更・取消) | ✅ 有効 | PlaceOrder / UpdateOrder / CancelOrder に必要 |
   | 約定情報通知(WebSocket) | ✅ 有効 | executionEvents(約定イベント)の購読に必要 |
   | 注文情報通知(WebSocket) | ✅ 有効 | orderEvents(注文イベント)の購読に必要 |
   | **出金・振替** | ❌ **無効** | コネクターは出金 API を一切使わない。キー漏洩時の被害を限定する |

   ※ WebSocket 系のパーミッションを付けずに発行したキーでは `POST /v1/ws-auth` のトークン取得が失敗する。後から会員ページの【API】-【編集】で追加できる。

3. 可能であれば会員ページの **IP アドレス制限**を有効にする(自宅/オフィスの固定 IP、AWS は NAT Gateway の EIP)
4. **テスト用と本番用で別のキーを発行**する(GMOコインにはテストネットが無いため、結合テストも本番口座で行う。テスト用キーは事故切り分けと失効運用のために分離する)
5. 表示された API キーとシークレットをその場で 1Password に保存する

## 2. ローカル: 1Password

### 2.0 既存の 1Password アイテムを使う(最短)

すでに API キーを 1Password に保存済みなら、テンプレート [env.1password.sample](../QuantConnect.GMOCoinBrokerage/env.1password.sample) を `.env.1password` にコピーし、**op:// 秘密参照を自分のアイテムに書き換えるだけ**でよい(アイテム名の空白はそのまま書ける):

```powershell
# Windows
Copy-Item QuantConnect.GMOCoinBrokerage/env.1password.sample QuantConnect.GMOCoinBrokerage/.env.1password
```

```bash
# macOS / Linux
cp QuantConnect.GMOCoinBrokerage/env.1password.sample QuantConnect.GMOCoinBrokerage/.env.1password
```

```bash
GMOCOIN_API_KEY="op://<vault>/<item>/username"
GMOCOIN_API_SECRET="op://<vault>/<item>/credential"
```

- フィールド名はアイテムのカテゴリで異なる: API Credential は `username` / `credential`、Login は `username` / `password`。1Password アプリでフィールドにマウスオーバー →「秘密参照をコピー」で正確な参照を取得できる
- 参照の疎通確認: `op read "op://<vault>/<item>/credential"`(初回は 1Password の認証プロンプトを許可)

あとは実行ラッパー経由でコマンドを起動するだけ:

```powershell
scripts\op-run.cmd dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
```

### 2.0b セットアップスクリプト(新規にアイテムを作る場合)

アイテムをまだ作っていない場合は、アイテム作成 → `.env.1password` の生成 → `op read` での疎通確認までを一括で行うスクリプトを使える(既存アイテムがある場合は §2.0 の直接編集で十分):

```powershell
# Windows(cmd / PowerShell どちらからでも可。既定: vault=Private, item=gmocoin-api-test)
scripts\setup-1password.cmd

# 本番用キーを別アイテムで作る場合
scripts\setup-1password.cmd -Item gmocoin-api-prod
```

```bash
# macOS / Linux / Git Bash
scripts/setup-1password.sh
scripts/setup-1password.sh --item gmocoin-api-prod
```

- API キー / シークレットは**非表示入力**で受け取り、1Password にのみ保存する(画面・履歴・ディスクに残らない)
- 既存アイテムがある場合は上書きせず、`.env.1password` の参照だけを再生成する
- 前提: 1Password CLI(Windows `winget install AgileBits.1Password.CLI` / macOS `brew install 1password-cli`)と、アプリの **CLI 統合**(§2.2)が有効なこと
- Windows 用 `.cmd` は内部で `powershell -ExecutionPolicy Bypass -File ...ps1` を呼ぶため、**ExecutionPolicy の変更なしで動く**(`.ps1` を直接実行する場合は `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` が必要なことがある)。`.sh` は macOS / Linux / Git Bash 専用

以降のコマンド実行は同梱のラッパー経由が最短:

```powershell
scripts\op-run.cmd dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
```

```bash
scripts/op-run.sh dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
```

### 2.1 アイテムの作成(手動で行う場合)

1Password アプリで:

- Vault: `Private`(任意。チーム利用なら専用 Vault を推奨)
- アイテム名: `gmocoin-api-test`(本番用は `gmocoin-api-prod`)
- カテゴリ: API Credential(または Login)
- フィールド:
  - `api-key` = 発行された API キー
  - `api-secret` = 発行されたシークレット(フィールド種別を「パスワード」にする)

CLI で作る場合(値はプロンプトで貼り付け):

```bash
op item create --category "API Credential" --vault Private --title gmocoin-api-test \
  api-key[text]="$(read -s -p 'API Key: ' k; echo $k)" \
  api-secret[password]="$(read -s -p 'API Secret: ' s; echo $s)"
```

### 2.2 CLI 連携の有効化(初回のみ)

1Password デスクトップアプリ → 設定(⚙)→ **開発者** → **1Password CLI と連携(Integrate with 1Password CLI)** を ON(`op` コマンドの認証がアプリの Windows Hello / 生体認証プロンプト経由になる)。Microsoft Store 版の 1Password でも同じ場所にある。

この設定が OFF のままだと、アカウントが `op account list` に見えていても `op whoami` が「not signed in」で失敗する(セットアップスクリプトはこの状態を検出して案内を出す)。

アプリ連携を使わない場合の代替(そのシェルのみ有効、約 30 分):

```powershell
# PowerShell(パスワードを対話入力し、セッション環境変数を設定)
Invoke-Expression $(op signin)
```

```bash
# macOS / Linux
eval $(op signin)
```

動作確認:

```bash
op read "op://Private/gmocoin-api-test/api-key"
```

### 2.3 `.env.1password`(ローカル専用、git 管理外)

op:// 参照は実値(シークレット)ではないが、**ボールト名・アイテム名(または ID)という環境固有のメタデータを含む**ため、リポジトリにはテンプレート [env.1password.sample](../QuantConnect.GMOCoinBrokerage/env.1password.sample) のみをコミットする。実ファイルは各マシンでサンプルからコピーして作る(`.env*` は .gitignore 済み。コピー手順とプレースホルダーの書き換えは §2.0 参照):

```bash
GMOCOIN_API_KEY="op://Private/gmocoin-api-test/api-key"
GMOCOIN_API_SECRET="op://Private/gmocoin-api-test/api-secret"
```

複数マシンで作業する場合は、マシンごとにこの手順で作成する(git pull では同期されない・されてはいけない)。

### 2.4 起動方法

`op run` が op:// 参照を実行時に解決し、**子プロセスの環境変数としてだけ**注入する(ディスクに書かれない)。同梱ラッパー(`scripts\op-run.cmd` / `scripts/op-run.sh`)は `op run --env-file=QuantConnect.GMOCoinBrokerage/.env.1password -- <command>` の短縮形:

```powershell
# Windows
scripts\op-run.cmd dotnet QuantConnect.Lean.Launcher.dll --environment live-gmocoin
```

```bash
# macOS / Linux
scripts/op-run.sh dotnet QuantConnect.Lean.Launcher.dll --environment live-gmocoin
```

疎通確認だけしたい場合(残高取得のワンショット):

```powershell
scripts\op-run.cmd dotnet run --project QuantConnect.GMOCoinBrokerage/tools/AssetsCheck
```

## 3. AWS: SSM パラメータストア

### 3.1 パラメータの登録

パス階層で環境を分離する(`prod` / `test`):

```bash
aws ssm put-parameter --name /lean/gmocoin/prod/api-key \
  --type SecureString --key-id alias/lean-gmocoin --value '<API_KEY>'

aws ssm put-parameter --name /lean/gmocoin/prod/api-secret \
  --type SecureString --key-id alias/lean-gmocoin --value '<API_SECRET>'
```

- `--key-id` は専用の KMS キー(`alias/lean-gmocoin`)を推奨。省略時はアカウント既定の `aws/ssm` キーが使われる
- シェル履歴に値を残したくない場合は `--value file:///dev/stdin` で標準入力から渡す

### 3.2 IAM(実行ロールに最小権限)

Lean を動かす ECS タスクロール / EC2 インスタンスロールにのみ:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "ssm:GetParameter",
      "Resource": "arn:aws:ssm:*:<ACCOUNT_ID>:parameter/lean/gmocoin/prod/*"
    },
    {
      "Effect": "Allow",
      "Action": "kms:Decrypt",
      "Resource": "arn:aws:kms:*:<ACCOUNT_ID>:key/<KEY_ID>"
    }
  ]
}
```

### 3.3 環境変数へのマップ

**ECS(推奨)** — タスク定義の `secrets` で直接マップ(コンテナ環境変数になる):

```json
"secrets": [
  { "name": "GMOCOIN_API_KEY",    "valueFrom": "arn:aws:ssm:ap-northeast-1:<ACCOUNT_ID>:parameter/lean/gmocoin/prod/api-key" },
  { "name": "GMOCOIN_API_SECRET", "valueFrom": "arn:aws:ssm:ap-northeast-1:<ACCOUNT_ID>:parameter/lean/gmocoin/prod/api-secret" }
]
```

**EC2 / systemd** — 起動スクリプトで取得して export:

```bash
export GMOCOIN_API_KEY=$(aws ssm get-parameter --with-decryption \
  --name /lean/gmocoin/prod/api-key --query Parameter.Value --output text)
export GMOCOIN_API_SECRET=$(aws ssm get-parameter --with-decryption \
  --name /lean/gmocoin/prod/api-secret --query Parameter.Value --output text)
exec dotnet QuantConnect.Lean.Launcher.dll --environment live-gmocoin
```

## 4. 運用ルール

- `config.json` の `gmocoin-api-key` / `gmocoin-api-secret` は**常に空のまま**にする(環境変数フォールバックが働く)
- 実値を含むファイル(`.env` など)を作った場合は必ず [.gitignore](../.gitignore) 対象にする。本リポジトリでは `.env*` を**すべて** ignore 済みで、コミット対象はテンプレート `env.1password.sample` のみ(§2.3)
- キーのローテーション: GMOコインで新キー発行 → 1Password / SSM の値を差し替え → プロセス再起動 → 旧キーを GMOコイン側で削除
- Private API のレート制限は先週の取引高による Tier 制(Tier 1: GET/POST とも 20 req/s)。コネクター内蔵のレートゲート(GET 10/s、POST 6/s)は Tier 1 の範囲内
- ログ・例外にキーが出ないことは実装側で担保済み(署名処理はヘッダー生成時のみシークレットを使用)

## 5. 動作確認チェックリスト

Windows(PowerShell):

```powershell
# 1) op が参照を解決できるか
(op read "op://Private/gmocoin-api-test/api-key").Substring(0, 8) + "..."

# 2) 環境変数が子プロセスに渡るか
scripts\op-run.cmd powershell -NoProfile -Command "$env:GMOCOIN_API_KEY.Substring(0, 8) + '...'"
```

macOS / Linux:

```bash
# 1) op が参照を解決できるか
op read "op://Private/gmocoin-api-test/api-key" | head -c 8; echo "..."

# 2) 環境変数が子プロセスに渡るか
scripts/op-run.sh printenv GMOCOIN_API_KEY | head -c 8; echo "..."
```

3) 残高取得(結合テスト第 1 段階)は、キー登録完了後に `AssetsCheck` のワンショット実行で確認する(§2.4)。

## 6. 結合テストツール

すべて `op run` 経由で実行する(§2.4 と同じ方式。Windows は `scripts\op-run.cmd <command>`、macOS / Linux は `scripts/op-run.sh <command>`)。

| ツール | 内容 | リスク |
|---|---|---|
| `tools/AssetsCheck`(パスはリポジトリルートから `QuantConnect.GMOCoinBrokerage/tools/...`) | 残高・アクティブ注文・private WebSocket トークンの取得 | なし(参照のみ) |
| `tools/StreamCheck` | プライベート WebSocket を購読し受信メッセージを表示(既定 60 秒) | なし(参照のみ) |
| `tools/OrderSmokeTest` | **実注文**の最小ロットライフサイクルテスト: post-only(SOK)指値買い(最良買い気配の 90%。GMO は市場から離れすぎた指値を ERR-5121 で拒否するため 50% 下は使えない。SOK により taker 約定は不可)→ ストリームで確認 → 即取消 | 最小(`--yes` 必須、約 100 円相当の指値が数秒間板に載る) |

```powershell
# ストリーム受信確認(60 秒監視。実行中に GMOコインアプリで注文操作をすると orderEvents が流れる)
scripts\op-run.cmd dotnet run --project QuantConnect.GMOCoinBrokerage/tools/StreamCheck

# 注文ライフサイクル確認(実注文を伴うため --yes と Enter 確認が必要)
scripts\op-run.cmd dotnet run --project QuantConnect.GMOCoinBrokerage/tools/OrderSmokeTest -- --yes
```

(macOS / Linux は `scripts/op-run.sh` に読み替え)

`OrderSmokeTest` の合格条件: 発注 → `orderEvents`(ORDERED)受信 → 取消 → `CANCELED` 受信 → REST 最終確認、の全段階が通ること。これが green なら Lean 本体(`live-gmocoin` 環境)での E2E に進める。
