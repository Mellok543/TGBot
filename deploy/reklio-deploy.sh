#!/usr/bin/env bash
set -Eeuo pipefail

SOURCE=/opt/reklio/source
BRANCH=feature/miniapp-typescript-foundation
API_DIR=/opt/reklio/api
WEB_DIR=/var/www/reklio/app
HEALTH_URL=https://api.reklio.site/api/health
WEB_URL=https://reklio.site

if [[ "$(id -u)" -ne 0 ]]; then
  echo "This script must be run via sudo." >&2
  exit 1
fi
for tool in git dotnet npm curl systemctl cp mktemp find; do
  command -v "$tool" >/dev/null || { echo "Missing: $tool" >&2; exit 1; }
done
# The Telegram OAuth secret arrives only via SSH stdin as base64.
# Configure before publishing so a failed secret setup cannot change live code.
LOGIN_SECRET_B64="$(cat)"
if [[ -z "$LOGIN_SECRET_B64" ]] || ! [[ "$LOGIN_SECRET_B64" =~ ^[A-Za-z0-9+/=]+$ ]]; then
  echo "Missing or invalid Telegram Login secret input" >&2
  exit 1
fi
if ! printf '%s' "$LOGIN_SECRET_B64" | base64 --decode >/dev/null 2>&1; then
  echo "Invalid Telegram Login secret encoding" >&2
  exit 1
fi
LOGIN_USER="$(systemctl show reklio-api.service -p User --value)"
LOGIN_GROUP="root"
if [[ -n "$LOGIN_USER" ]]; then
  LOGIN_GROUP="$(id -gn "$LOGIN_USER")"
fi
install -d -m 700 /etc/reklio
install -d -m 700 /etc/systemd/system/reklio-api.service.d
install -d -m 700 /opt/reklio/auth-keys
if [[ -n "$LOGIN_USER" ]]; then chown "$LOGIN_USER:$LOGIN_GROUP" /opt/reklio/auth-keys; fi
TMP_ENV="$(mktemp /etc/reklio/.telegram-login.XXXXXXXX)"
chmod 640 "$TMP_ENV"
if [[ -n "$LOGIN_USER" ]]; then chown root:"$LOGIN_GROUP" "$TMP_ENV"; fi
printf 'TELEGRAM_LOGIN_CLIENT_ID=8857548722\nTELEGRAM_LOGIN_SECRET_B64=%s\nREKLIO_AUTH_KEY_DIRECTORY=/opt/reklio/auth-keys\n' "$LOGIN_SECRET_B64" > "$TMP_ENV"
mv -f "$TMP_ENV" /etc/reklio/telegram-login.env
printf '[Service]\nEnvironmentFile=/etc/reklio/telegram-login.env\n' > /etc/systemd/system/reklio-api.service.d/telegram-login.conf
chmod 644 /etc/systemd/system/reklio-api.service.d/telegram-login.conf
systemctl daemon-reload
unset LOGIN_SECRET_B64

if [[ -n "$(git -C "$SOURCE" status --porcelain)" ]]; then
  echo "Uncommitted changes in $SOURCE; deployment stopped to protect local work." >&2
  exit 1
fi

git -C "$SOURCE" fetch origin "$BRANCH"
git -C "$SOURCE" switch "$BRANCH"
git -C "$SOURCE" merge --ff-only "origin/$BRANCH"

STAGE="$(mktemp -d /opt/reklio/.deploy.XXXXXXXX)"
ROLLBACK_NEEDED=0

clear_directory() {
  local directory="$1"
  find "$directory" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +
}

wait_for_api() {
  local attempt
  for attempt in $(seq 1 30); do
    if systemctl is-active --quiet reklio-api.service &&
       curl --fail --silent --show-error --max-time 5 "$HEALTH_URL" >/dev/null 2>&1; then
      echo "API is ready"
      return 0
    fi
    sleep 2
  done
  return 1
}

rollback() {
  echo "Deployment failed; restoring previous API and Mini App..." >&2
  systemctl stop reklio-api.service || true
  clear_directory "$API_DIR"
  cp -a "$STAGE/previous-api/." "$API_DIR/"
  clear_directory "$WEB_DIR"
  cp -a "$STAGE/previous-web/." "$WEB_DIR/"
  systemctl start reklio-api.service || true
  if wait_for_api; then
    echo "Rollback completed; previous API is healthy." >&2
  else
    echo "CRITICAL: Rollback completed but API health check failed." >&2
  fi
}

cleanup() {
  local status=$?
  trap - EXIT
  if [[ "$status" -ne 0 && "$ROLLBACK_NEEDED" -eq 1 ]]; then
    rollback || true
  fi
  rm -rf -- "$STAGE"
  exit "$status"
}
trap cleanup EXIT

cd "$SOURCE/miniapp"
npm install --no-package-lock --no-audit --no-fund
VITE_API_URL=https://api.reklio.site npm run build

cd "$SOURCE"
dotnet publish src/TelegramManager.Api/TelegramManager.Api.csproj \
  -c Release -o "$STAGE/api"
mkdir -p "$STAGE/web" "$STAGE/previous-api" "$STAGE/previous-web"
cp -a miniapp/dist/. "$STAGE/web/"

# Back up both published applications before modifying production.
cp -a "$API_DIR/." "$STAGE/previous-api/"
cp -a "$WEB_DIR/." "$STAGE/previous-web/"

# Keep operator-managed systemd worker flags across deployments.
# Never enable or disable publication as a side effect of updating code.

ROLLBACK_NEEDED=1
systemctl stop reklio-api.service
clear_directory "$API_DIR"
cp -a "$STAGE/api/." "$API_DIR/"
clear_directory "$WEB_DIR"
cp -a "$STAGE/web/." "$WEB_DIR/"
systemctl start reklio-api.service

echo "Waiting for Reklio API..."
if ! wait_for_api; then
  echo "ERROR: API did not become ready." >&2
  exit 1
fi

# Validate that nginx serves real JS/CSS assets, not SPA HTML fallbacks.
# A 200 response alone is insufficient: a wrong document root can serve index.html.
for extension in js css; do
  asset="$(find "$WEB_DIR/assets" -maxdepth 1 -type f -name "*.$extension" -print -quit)"
  if [[ -z "$asset" ]]; then
    echo "ERROR: Missing $extension build asset" >&2
    exit 1
  fi
  asset_name="${asset##*/}"
  response_type="$(curl --fail --silent --show-error --max-time 12 -o /dev/null -w '%{content_type}' "$WEB_URL/assets/$asset_name")"
  case "$extension:$response_type" in
    js:*javascript*|css:text/css*) echo "Public $extension asset OK ($response_type)" ;;
    *) echo "ERROR: Public $extension asset served as $response_type; check nginx root /var/www/reklio/app" >&2; exit 1 ;;
  esac
done

ROLLBACK_NEEDED=0
echo "Reklio deployed successfully: $(git -C "$SOURCE" rev-parse --short HEAD)"
