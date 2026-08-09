#!/bin/sh
# One-time bootstrap for an EC2 instance (Amazon Linux 2023, x86_64) that will
# run the GMO Coin momentum rotation live 24/7. Run as ec2-user AFTER the repo
# has been placed at /opt/lean-gmocoin/repo (git clone or scp; see docs/AWS.md).
#
#   sudo sh /opt/lean-gmocoin/repo/deploy/aws/setup-ec2.sh
#
# Steps: install docker -> build the plugin DLL (inside the .NET 10 SDK image,
# no host SDK needed) -> merge the gmocoin data rows -> install and enable the
# systemd unit. API keys are NOT stored here; the unit fetches them from SSM
# Parameter Store at every start.
set -eu
BASE=/opt/lean-gmocoin
REPO="$BASE/repo"
[ -d "$REPO" ] || { echo "ERROR: repo not found at $REPO"; exit 1; }

echo "== install docker =="
if ! command -v docker >/dev/null 2>&1; then
    dnf install -y docker
fi
systemctl enable --now docker

echo "== pull images =="
docker pull quantconnect/lean:latest
docker pull mcr.microsoft.com/dotnet/sdk:10.0

echo "== build plugin DLL (inside the .NET SDK container) =="
docker run --rm -v "$REPO:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build QuantConnect.GMOCoinBrokerage

echo "== merge gmocoin rows into the image's symbol-properties / market-hours =="
mkdir -p "$BASE/data/symbol-properties" "$BASE/data/market-hours"
# invoke via sh: zip-based deployments lose the executable bit
docker run --rm -v "$REPO:/repo:ro" -v "$BASE/data:/out" --entrypoint /bin/sh quantconnect/lean:latest -c \
    'cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ &&
     cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ &&
     sh /repo/scripts/install-gmocoin-data.sh /out'

echo "== install the credential fetcher (SSM Parameter Store -> env file) =="
cat > /usr/local/bin/lean-gmocoin-env.sh <<'FETCH'
#!/bin/sh
# Writes /run/lean-gmocoin.env (tmpfs, root-only) from SSM Parameter Store.
# Requires an instance role with ssm:GetParameter + kms:Decrypt on the params.
set -eu
PREFIX="${LEAN_GMOCOIN_SSM_PREFIX:-/lean/gmocoin/prod}"
KEY=$(aws ssm get-parameter --with-decryption --name "$PREFIX/api-key" --query Parameter.Value --output text)
SECRET=$(aws ssm get-parameter --with-decryption --name "$PREFIX/api-secret" --query Parameter.Value --output text)
umask 077
{
    echo "GMOCOIN_API_KEY=$KEY"
    echo "GMOCOIN_API_SECRET=$SECRET"
} > /run/lean-gmocoin.env
FETCH
chmod 700 /usr/local/bin/lean-gmocoin-env.sh

echo "== install and enable the systemd unit =="
cp "$REPO/deploy/aws/lean-gmocoin.service" /etc/systemd/system/lean-gmocoin.service
systemctl daemon-reload
systemctl enable lean-gmocoin

echo
echo "Setup complete. Start with:  sudo systemctl start lean-gmocoin"
echo "Follow the logs with:        journalctl -u lean-gmocoin -f"
