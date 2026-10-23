#!/usr/bin/env bash
set -Eeuo pipefail

SOURCE=/opt/reklio/source
BRANCH=feature/miniapp-typescript-foundation
API_DIR=/opt/reklio/api
WEB_DIR=/var/www/reklio/app

if [[ "$(id -u)" -ne 0 ]]; then
  echo "This script must be run via sudo." >&2
  exit 1
fi
for tool in git dotnet npm curl systemctl cp mktemp; do
  command -v "$tool" >/dev/null || { echo "Missing: $tool" >&2; exit 1; }
done
if [[ -n "$(git -C "$SOURCE" status --porcelain)" ]]; then
  echo "Uncommitted changes in $SOURCE; deployment stopped to protect local work." >&2
  exit 1
fi

git -C "$SOURCE" fetch origin "$BRANCH"
git -C "$SOURCE" switch "$BRANCH"
git -C "$SOURCE" merge --ff-only "origin/$BRANCH"

STAGE="$(mktemp -d /opt/reklio/.deploy.XXXXXXXX)"
trap 'rm -rf "$STAGE"' EXIT
cd "$SOURCE/miniapp"
npm ci --no-audit --no-fund
VITE_API_URL=https://api.reklio.site npm run build

cd "$SOURCE"
dotnet publish src/TelegramManager.Api/TelegramManager.Api.csproj \
  -c Release -o "$STAGE/api"
mkdir -p "$STAGE/web"
cp -a miniapp/dist/. "$STAGE/web/"

# Keep a backup of the last published API before replacing any files.
mkdir -p /opt/reklio/api-previous
cp -a "$API_DIR/." /opt/reklio/api-previous/

systemctl stop reklio-api.service
cp -a "$STAGE/api/." "$API_DIR/"
if ! systemctl start reklio-api.service; then
  echo "API failed to start. Restoring previous published files." >&2
  cp -a /opt/reklio/api-previous/. "$API_DIR/"
  systemctl start reklio-api.service || true
  exit 1
fi

# Only update the public UI once the API service has started.
cp -a "$STAGE/web/." "$WEB_DIR/"
systemctl is-active --quiet reklio-api.service
curl --fail --silent --show-error --max-time 10 https://api.reklio.site/api/health
echo
echo "Reklio deployed successfully: $(git -C "$SOURCE" rev-parse --short HEAD)"
