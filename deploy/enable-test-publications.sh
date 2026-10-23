#!/usr/bin/env bash
set -Eeuo pipefail
CHANNEL_ID=-1004357250006
OVERRIDE=/etc/systemd/system/reklio-api.service.d/30-test-publications.conf
HEALTH=http://127.0.0.1:5080/api/health
DB=s2_telegram_manager
[[ "$(id -u)" == 0 ]] || { echo "Run as root" >&2; exit 1; }
systemctl is-active --quiet reklio-api.service || { echo "API is not running" >&2; exit 1; }
curl --fail --silent --show-error --max-time 8 "$HEALTH" >/dev/null
[[ -r /home/reklio-deploy/.my.cnf ]] || { echo "Missing MariaDB config" >&2; exit 1; }
table_exists="$(sudo -u reklio-deploy mariadb --batch --skip-column-names -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='$DB' AND table_name='reklio_publications'")"
[[ "$table_exists" == 1 ]] || { echo "Publications migration is missing" >&2; exit 1; }
channel_exists="$(sudo -u reklio-deploy mariadb --batch --skip-column-names -e "SELECT COUNT(*) FROM $DB.channels WHERE telegram_id=$CHANNEL_ID")"
[[ "$channel_exists" == 1 ]] || { echo "Test channel not found" >&2; exit 1; }
[[ ! -e "$OVERRIDE" ]] || { echo "Override already exists; refusing to overwrite it" >&2; exit 1; }
mkdir -p "${OVERRIDE%/*}"
trap 'echo "Activation failed; restoring API settings" >&2; rm -f "$OVERRIDE"; systemctl daemon-reload; systemctl restart reklio-api.service || true' ERR
cat > "$OVERRIDE" <<'EOF'
[Service]
Environment="REKLIO_PUBLISH_ENABLED=true"
Environment="REKLIO_ALLOW_TEST_PUBLICATIONS=true"
Environment="REKLIO_TEST_CHANNEL_ID=-1004357250006"
EOF
systemctl daemon-reload
systemctl restart reklio-api.service
healthy=0
for i in $(seq 1 15); do
  if systemctl is-active --quiet reklio-api.service && curl --fail --silent --max-time 5 "$HEALTH" >/dev/null; then healthy=1; break; fi
  sleep 2
done
[[ "$healthy" == 1 ]] || { echo "API failed after activation" >&2; false; }
trap - ERR
echo "Test publication worker enabled; API healthy."
echo "Check logs: journalctl -u reklio-api -n 50 --no-pager"
