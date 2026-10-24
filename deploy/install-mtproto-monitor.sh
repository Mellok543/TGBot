#!/usr/bin/env bash
# Run as root on the Reklio VPS after reviewing the configuration.
set -Eeuo pipefail
[[ "$(id -u)" -eq 0 ]] || { echo "Must run as root" >&2; exit 1; }
SOURCE=/opt/reklio/source
SERVICE=/etc/systemd/system/reklio-mtproto-monitor.service
ENVFILE=/etc/reklio/mtproto-monitor.env
DB=s2_telegram_manager

for name in python3 mariadb mariadb-dump systemctl; do command -v "$name" >/dev/null || { echo "Missing $name" >&2; exit 1; }; done
test -r "$ENVFILE" || { echo "Create root-owned $ENVFILE before setup" >&2; exit 1; }
[[ "$(stat -c '%a' "$ENVFILE")" == 600 ]] || { echo "Environment file must have mode 600" >&2; exit 1; }
for key in TELEGRAM_API_ID TELEGRAM_API_HASH BOT_TOKEN DATABASE_URL; do
  grep -qE "^$key=" "$ENVFILE" || { echo "Missing $key in environment file" >&2; exit 1; }
done
# Do not automatically enable live checks during installation.
grep -q '^REKLIO_MTPROTO_MONITOR_ENABLED=false$' "$ENVFILE" || {
  echo "Set REKLIO_MTPROTO_MONITOR_ENABLED=false for initial installation" >&2; exit 1;
}
test -r "$SOURCE/deploy/migrations/005_reklio_post_monitor.sql"
install -d -m 0700 /var/backups/reklio
BACKUP="/var/backups/reklio/post-monitor-before-$(date -u +%Y%m%dT%H%M%SZ).sql"
# The root OS account must have a working MariaDB local admin login.
mariadb-dump --single-transaction --routines --triggers --events "$DB" > "$BACKUP"
test -s "$BACKUP"
chmod 0600 "$BACKUP"
mariadb "$DB" < "$SOURCE/deploy/migrations/005_reklio_post_monitor.sql"
python3 -m venv /opt/reklio/mtproto-venv
/opt/reklio/mtproto-venv/bin/python -m pip install --disable-pip-version-check 'telethon==1.42.0' 'PyMySQL==1.1.1'
install -o root -g root -m 0644 "$SOURCE/deploy/reklio-mtproto-monitor.service" "$SERVICE"
systemctl daemon-reload
systemctl disable reklio-mtproto-monitor.service >/dev/null 2>&1 || true
echo "Installed and configured; service intentionally disabled."
echo "DB backup: $BACKUP"
echo "After separate validation, set ENABLED=true in $ENVFILE and run:"
echo "systemctl enable --now reklio-mtproto-monitor.service"
