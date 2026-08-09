# AWS 常時稼働ガイド(EC2 + systemd + SSM)

モーメンタムローテーション([examples/gmocoin_momentum_rotation.py](../examples/gmocoin_momentum_rotation.py))を AWS 上で 24/7 稼働させる手順。構成は最小限:

```
EC2 (Amazon Linux 2023, t3.small, 東京リージョン)
 └─ systemd: lean-gmocoin.service   ← 落ちたら 60 秒後に自動再起動
     └─ docker: quantconnect/lean:latest
         └─ scripts/live-container.sh → Lean Launcher (live-gmocoin)
 API キー: SSM Parameter Store (SecureString) → 起動のたびに tmpfs の env ファイルへ
```

**再起動安全性**: 戦略はローカル状態を持たない。再起動のたびに Lean が残高・未約定注文をブローカレッジから復元し、モーメンタム指標は GMO の KLine 履歴から再ウォームアップされる(検証済みの `BrokerageHistoryProvider` 経由)。

**免責**: サンプル戦略であり投資助言ではない。必ず少額で挙動を確認してから運用すること。

---

## 0. 事前にローカルで動作確認

AWS に載せる前に、同じコンテナ構成でローカル実行して 1〜2 回のリバランスを観察する:

```powershell
scripts\run-live.cmd
```

(macOS / Linux: `scripts/run-live.sh`。Ctrl+C で停止)

## 1. SSM パラメータストアにキーを登録

**本番用の API キーを新規発行**(権限は [SETUP.md](SETUP.md) §1 と同じ: 参照 + 注文 + WebSocket 通知、**出金は無効**)し、東京リージョンに登録:

```bash
aws ssm put-parameter --region ap-northeast-1 --name /lean/gmocoin/prod/api-key \
  --type SecureString --value '<API_KEY>'

aws ssm put-parameter --region ap-northeast-1 --name /lean/gmocoin/prod/api-secret \
  --type SecureString --value '<API_SECRET>'
```

## 2. IAM ロール(EC2 インスタンスプロファイル)

ロール `lean-gmocoin-ec2` を作成し、信頼ポリシーは EC2、権限は最小限:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "ssm:GetParameter",
      "Resource": "arn:aws:ssm:ap-northeast-1:<ACCOUNT_ID>:parameter/lean/gmocoin/prod/*"
    }
  ]
}
```

(既定の `aws/ssm` KMS キーを使う場合は `kms:Decrypt` の明示付与は不要。専用 KMS キーなら [SETUP.md](SETUP.md) §3.2 のように追加)

## 3. EC2 インスタンスの起動

- リージョン: **ap-northeast-1(東京)** — GMO コインへのレイテンシ最小
- AMI: Amazon Linux 2023 (x86_64) ※ lean イメージは amd64 のため Graviton (arm64) は不可
- タイプ: `t3.small`(2 vCPU / 2GB。Lean live + Python で 1GB 前後使用)
- ストレージ: 20 GB gp3
- IAM ロール: 手順 2 のロールを割り当て
- セキュリティグループ: **インバウンド全閉**(SSH も使わず SSM Session Manager 推奨。使うなら SSH を自 IP のみ)
- GMO コイン側で **IP 制限**を使う場合は Elastic IP を割り当ててそのアドレスを登録

## 4. デプロイ

接続(SSM Session Manager または SSH)して:

```bash
# リポジトリを配置(GitHub に push 済みなら clone、なければローカルから scp)
sudo mkdir -p /opt/lean-gmocoin
sudo git clone https://github.com/aobathree/Lean.Brokerages.GMOCoin.git /opt/lean-gmocoin/repo

# ブートストラップ(docker インストール → プラグインビルド → データマージ → systemd 登録)
sudo sh /opt/lean-gmocoin/repo/deploy/aws/setup-ec2.sh

# 起動
sudo systemctl start lean-gmocoin
```

ローカルから scp で送る場合(git 不使用):

```powershell
# Windows 側(bin/obj を除いて転送)
scp -r -o ProxyCommand=none D:\Lean.Brokerages.GMOCoin ec2-user@<EIP>:/tmp/repo
# EC2 側で sudo mv /tmp/repo /opt/lean-gmocoin/repo
```

## 5. 運用

```bash
# ログ追跡(リバランスは毎日 09:10 / 09:13 JST に出る)
journalctl -u lean-gmocoin -f

# 状態確認 / 停止 / 再起動
systemctl status lean-gmocoin
sudo systemctl stop lean-gmocoin
sudo systemctl restart lean-gmocoin

# 戦略やプラグインを更新したら
cd /opt/lean-gmocoin/repo && sudo git pull
sudo sh deploy/aws/setup-ec2.sh          # 再ビルド + データ再マージ
sudo systemctl restart lean-gmocoin
```

- **停止 = ポジション放置**である点に注意: サービスを止めても保有 BTC 等は売られない。手仕舞いしたい場合は GMO コインアプリで手動売却するか、停止前にアルゴリズムを Liquidate する
- 再起動ループ保護: 30 分に 10 回以上落ちると systemd が起動を止める(`systemctl reset-failed lean-gmocoin` で解除)
- キーのローテーション: SSM の値を差し替えて `systemctl restart lean-gmocoin`(起動のたびに SSM から再取得)
- 料金目安: t3.small + 20GB gp3 + EIP で月 ~3,000 円前後(東京、2026 年時点)

## 6. 監視(任意)

- CloudWatch Agent で journald の `lean-gmocoin` ユニットを CloudWatch Logs へ転送し、`ERROR` のメトリクスフィルタ + SNS 通知
- もっと軽く済ませるなら: `journalctl -u lean-gmocoin --since -1h | grep -c ERROR` を cron で確認して通知
- Lean 側の異常(ブローカレッジ切断)は `WebSocketClientWrapper` が自動再接続し、エンジンごと落ちた場合は systemd が再起動する

## 7. トラブルシューティング

| 症状 | 確認 |
|---|---|
| 起動直後に落ちる | `journalctl -u lean-gmocoin -n 100`。SSM 取得失敗なら IAM ロール / リージョン / パラメータ名を確認 |
| ERR-5012(認証失敗) | SSM の値が正しいか、GMO の IP 制限に EIP が登録されているか |
| データが来ない | GMO のメンテナンス時間(定期: 水曜 15:00–17:00 JST 頃)は WebSocket が切断される。自動再接続で復帰 |
| 注文が拒否される(ERR-5126 等) | 口座残高と最小注文数量(README の対応銘柄表)を確認 |
