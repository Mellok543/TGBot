# Reklio MTProto public-channel presence monitor

This monitor has a strictly opt-in production activation. The original GitHub Action confirmed `PRESENT` for `@TestChanel54/13` and `MISSING` after manual deletion.

## What it does

- Logs in as the existing Reklio Bot with Telethon MTProto (bot token + api_id + api_hash).
- Reads `reklio_publications` and checks at most 50 oldest-unchecked *active*, public-channel posts every 300 seconds.
- Never scans private channels (no public `channels.username`), creates messages or deletes posts.
- When Telegram returns `MessageEmpty` in **two checks at least 60 seconds apart**, and the paid placement period is still active, it records an order event, opens an administrative dispute, sends notifications and blocks real payout eligibility. Test orders remain `NotEligible`.
- Errors and missing access are **not** counted as deletions.
- Does not take over YooKassa payment workflows or move funds.
- The current check tests the message's existence, **not** whether it was edited or the channel owner still has the expected access.

## Setup on VPS (controlled, manual activation)

1. Back up MariaDB, then apply `deploy/migrations/005_reklio_post_monitor.sql` once. The table must exist **before** the monitor starts.
2. Install a Python 3 virtual environment at `/opt/reklio/mtproto-venv` and install `telethon==1.42.0` and `pymysql`.
3. Copy `deploy/reklio-mtproto-monitor.service` to `/etc/systemd/system/reklio-mtproto-monitor.service`.
4. Create a root-owned mode-0600 `/etc/reklio/mtproto-monitor.env`:
   ```
   REKLIO_MTPROTO_MONITOR_ENABLED=false
   TELEGRAM_API_ID=...
   TELEGRAM_API_HASH=...
   BOT_TOKEN=...
   DATABASE_URL='Server=...;Port=3306;User=...;Password=...;Database=...'
   ```
   The database account needs `SELECT` over channels/publications/orders and permission to update orders and monitoring table, insert into notification_outbox and ad_order_events. Do not put secrets in Git or log output. GitHub Actions environment secrets are not automatically visible to VPS systemd.
5. Run `systemctl daemon-reload`. Use `systemctl start reklio-mtproto-monitor` with the disabled flag first, confirm it exits cleanly. Then set flag `true` and `systemctl enable --now reklio-mtproto-monitor`.

The VPS can be updated via the existing Reklio branch deploy, which fetches the Python script, but **this does not automatically provision the service, DB migration, Python packages or secrets**.

## Operational safeguards

- Restrict public channel MTProto checks to bot-accessible channels.
- Keep a manual-review path for ambiguous Telegram responses.
- Verify using a controlled, paid test order; a random deleted Telegram message is not sufficient to exercise the database state machine.
- Verify only one instance of this monitor runs at a time. A shared distributed lock is not implemented.
- No real payout or refund is activated.
