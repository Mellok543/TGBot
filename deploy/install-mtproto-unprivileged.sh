#!/usr/bin/env bash
# Safe, unprivileged installation: secrets provided on stdin by GitHub Actions.
set -Eeuo pipefail
umask 077
SOURCE=/opt/reklio/source
DB=s2_telegram_manager
HOME_DIR="$HOME/.local/share/reklio-mtproto"
test -r "$HOME/.my.cnf"
for cmd in python3 mariadb mariadb-dump crontab flock; do command -v "$cmd" >/dev/null || { echo "Missing $cmd" >&2; exit 1; }; done
read -r API_ID
read -r API_HASH
read -r BOT_TOKEN
[[ "$API_ID" =~ ^[0-9]+$ && -n "$API_HASH" && -n "$BOT_TOKEN" ]] || { echo "MTProto credentials absent or invalid"; exit 1; }
test -r "$SOURCE/deploy/reklio_mtproto_monitor.py"
mkdir -p "$HOME_DIR" "$HOME/reklio-db-backups"
chmod 700 "$HOME_DIR" "$HOME/reklio-db-backups"
BACKUP="$HOME/reklio-db-backups/mtproto-$(date -u +%Y%m%dT%H%M%SZ).sql"
mariadb-dump --defaults-extra-file="$HOME/.my.cnf" --single-transaction "$DB" > "$BACKUP"
test -s "$BACKUP"
mariadb --defaults-extra-file="$HOME/.my.cnf" "$DB" < "$SOURCE/deploy/migrations/005_reklio_post_monitor.sql"
test -f "$HOME_DIR/vendor/telethon/__init__.py" || { echo "Vendored Telethon not staged" >&2; exit 1; }
test -f "$HOME_DIR/vendor/pymysql/__init__.py" || { echo "Vendored PyMySQL not staged" >&2; exit 1; }
printf 'TELEGRAM_API_ID=%q\nTELEGRAM_API_HASH=%q\nBOT_TOKEN=%q\nREKLIO_MTPROTO_MONITOR_ENABLED=true\n' "$API_ID" "$API_HASH" "$BOT_TOKEN" > "$HOME_DIR/secrets.env"
chmod 600 "$HOME_DIR/secrets.env"
cat > "$HOME_DIR/run.sh" <<'RUNNER'
#!/usr/bin/env bash
set -Eeuo pipefail
BASE="$HOME/.local/share/reklio-mtproto"
set -a
source "$BASE/secrets.env"
set +a
export PYTHONPATH="$BASE/vendor"
exec /usr/bin/python3 /opt/reklio/source/deploy/reklio_mtproto_monitor.py --once
RUNNER
chmod 700 "$HOME_DIR/run.sh"
# Preserve other cron entries and maintain exactly one Reklio monitor job.
CRON_LINE="*/5 * * * * /usr/bin/flock -n $HOME_DIR/monitor.lock $HOME_DIR/run.sh >> $HOME_DIR/monitor.log 2>&1 # reklio-mtproto"
(crontab -l 2>/dev/null || true) | grep -v '# reklio-mtproto' > "$HOME_DIR/crontab.new" || true
printf '%s\n' "$CRON_LINE" >> "$HOME_DIR/crontab.new"
crontab "$HOME_DIR/crontab.new"
# One safe run now; on errors the schedule remains in place for monitoring.
"$HOME_DIR/run.sh"
echo "Installed public-post monitor as a 5-minute user cron task."
echo "Backup created at $BACKUP"
echo "To inspect: tail -n 50 $HOME_DIR/monitor.log"
