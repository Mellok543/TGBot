#!/usr/bin/env python3
"""Reklio public-channel MTProto monitor. No writes to Telegram."""
import asyncio
import argparse
import configparser
from pathlib import Path
import logging
import os
from datetime import datetime, timezone

import pymysql
from telethon import TelegramClient, functions, types
from telethon.errors import RPCError
from telethon.sessions import StringSession

LOG = logging.getLogger("reklio-monitor")
INTERVAL = 300
CONFIRM_GAP_SECONDS = 60
# Never interpret a Telegram RPC/network error as proof of deletion.


def database_kwargs():
    connection = os.getenv("DATABASE_URL")
    if connection:
        parts = dict(item.split("=", 1) for item in connection.split(";") if "=" in item)
        parts = {key.strip().lower().replace(" ", ""): value.strip() for key, value in parts.items()}
    else:
        settings = configparser.ConfigParser(interpolation=None)
        if not settings.read(str(Path.home() / ".my.cnf")) or not settings.has_section("client"):
            raise RuntimeError("No DATABASE_URL and no ~/.my.cnf [client] section")
        parts = {key.strip().lower().replace("-", ""): value for key,value in settings.items("client")}
        parts["database"] = "s2_telegram_manager"
    def val(*names, default=None):
        return next((parts[name] for name in names if name in parts), default)
    return dict(host=val("server", "host", default="localhost"),
                port=int(val("port", default="3306")),
                user=val("user", "userid", "uid"),
                password=val("password", "pwd"),
                database=val("database", "initialcatalog"),
                charset="utf8mb4", autocommit=False,
                connect_timeout=10, read_timeout=15, write_timeout=15)


def candidates(db):
    with db.cursor() as cur:
        cur.execute("""
            SELECT p.order_id, c.username, p.telegram_message_id
            FROM reklio_publications p
            JOIN ad_requests a ON a.id=p.order_id
            JOIN channels c ON c.telegram_id=p.channel_id
            LEFT JOIN reklio_post_monitor m ON m.order_id=p.order_id
            WHERE p.state='Published' AND p.telegram_message_id IS NOT NULL
              AND p.deleted_at IS NULL AND p.delete_after_hours>0
              AND a.status='Published'
              AND c.username IS NOT NULL AND c.username<>''
              AND p.published_at<=UTC_TIMESTAMP()
              AND DATE_ADD(p.published_at, INTERVAL p.delete_after_hours HOUR)>DATE_ADD(UTC_TIMESTAMP(), INTERVAL 60 SECOND)
            ORDER BY COALESCE(m.checked_at, '1970-01-01') ASC LIMIT 50
        """)
        return cur.fetchall()


def record(db, order_id, status):
    now = datetime.now(timezone.utc).replace(tzinfo=None)
    with db.cursor() as cur:
        cur.execute("""
            INSERT INTO reklio_post_monitor(order_id,last_result,checked_at,missing_count,first_missing_at)
            VALUES(%s,%s,%s,%s,%s)
            ON DUPLICATE KEY UPDATE
              missing_count=IF(VALUES(last_result)='MISSING',
                 IF(last_result='MISSING' AND TIMESTAMPDIFF(SECOND,checked_at,VALUES(checked_at))>=%s,
                     missing_count+1,IF(last_result='MISSING',missing_count,1)),0),
              first_missing_at=IF(VALUES(last_result)='MISSING',
                 IF(last_result='MISSING',first_missing_at,VALUES(first_missing_at)),NULL),
              last_result=VALUES(last_result),checked_at=VALUES(checked_at)
        """, (order_id, status, now, 1 if status == "MISSING" else 0,
              now if status == "MISSING" else None, CONFIRM_GAP_SECONDS))
        if status != "MISSING":
            db.commit()
            return
        # Lock order before final action and ensure automated deletion has not begun.
        cur.execute("""
            SELECT m.missing_count FROM reklio_post_monitor m
            JOIN ad_requests a ON a.id=m.order_id
            JOIN reklio_publications p ON p.order_id=a.id
            WHERE m.order_id=%s AND m.missing_count>=2
              AND a.status='Published' AND p.state='Published'
              AND p.deleted_at IS NULL AND a.payout_status NOT IN ('Paid','Refunded')
              AND DATE_ADD(p.published_at, INTERVAL p.delete_after_hours HOUR)>UTC_TIMESTAMP()
            FOR UPDATE
        """, (order_id,))
        if cur.fetchone() is None:
            db.commit()
            return
        cur.execute("""
            UPDATE ad_requests SET status='Disputed',payout_status=IF(is_test_order=TRUE,'NotEligible','Blocked'),
              dispute_reason='Рекламная публикация удалена досрочно (MTProto, повторная проверка)',
              disputed_at=UTC_TIMESTAMP(),
              last_error='MTProto: пост отсутствует при двух последовательных проверках'
            WHERE id=%s AND status='Published' AND payout_status NOT IN ('Paid','Refunded')
        """, (order_id,))
        if cur.rowcount:
            cur.execute("""
                INSERT INTO ad_order_events(order_id,actor_id,event_type,details)
                VALUES(%s,NULL,'EarlyDeletionDetected','MTProto: две проверки MISSING')
            """, (order_id,))
            cur.execute("""
                INSERT IGNORE INTO notification_outbox(recipient_id,order_id,body)
                SELECT advertiser_id,id,CONCAT('Заказ #',id,': рекламный пост отсутствует до окончания срока. Открыта проверка.')
                  FROM ad_requests WHERE id=%s
                UNION ALL
                SELECT owner_id,id,CONCAT('Заказ #',id,': обнаружено досрочное удаление рекламы. Открыта проверка.')
                  FROM ad_requests WHERE id=%s
            """, (order_id, order_id))
            LOG.warning("Early deletion confirmed for order %s", order_id)
    db.commit()


def view_candidates(db):
    # Measure posts only after they have had a full 24 hours to accumulate views.
    # Bound samples per run to avoid excessive Telegram API traffic.
    with db.cursor() as cur:
        cur.execute("""
            SELECT p.channel_id,c.username,p.message_id
            FROM channel_observed_posts p
            JOIN channels c ON c.telegram_id=p.channel_id
            LEFT JOIN reklio_post_views v ON v.channel_id=p.channel_id AND v.message_id=p.message_id
            WHERE c.is_listed=TRUE AND c.username IS NOT NULL AND c.username<>''
              AND p.posted_at BETWEEN UTC_TIMESTAMP()-INTERVAL 30 DAY AND UTC_TIMESTAMP()-INTERVAL 24 HOUR
              AND v.message_id IS NULL
            ORDER BY p.posted_at DESC LIMIT 15
        """)
        return cur.fetchall()


async def collect_views(client, db):
    with db.cursor() as cur:
        cur.execute("""
            CREATE TABLE IF NOT EXISTS reklio_post_views (
                channel_id BIGINT NOT NULL,
                message_id INT NOT NULL,
                views INT NOT NULL,
                observed_at DATETIME NOT NULL,
                PRIMARY KEY(channel_id,message_id),
                INDEX idx_channel_views (channel_id,observed_at)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4
        """)
    db.commit()
    for channel_id, username, message_id in view_candidates(db):
        try:
            peer = await client.get_input_entity(username if username.startswith("@") else "@"+username)
            response = await client(functions.channels.GetMessagesRequest(
                channel=peer,id=[types.InputMessageID(id=int(message_id))]))
            message = next((m for m in response.messages if getattr(m,"id",None)==int(message_id)),None)
            views = getattr(message,"views",None)
            if isinstance(views,int) and views>=0:
                with db.cursor() as cur:
                    cur.execute("""
                        INSERT INTO reklio_post_views(channel_id,message_id,views,observed_at)
                        VALUES(%s,%s,%s,UTC_TIMESTAMP())
                        ON DUPLICATE KEY UPDATE views=GREATEST(views,VALUES(views)),observed_at=VALUES(observed_at)
                    """,(channel_id,message_id,views))
                db.commit()
        except (RPCError,ValueError,OSError,TimeoutError) as exc:
            db.rollback()
            LOG.warning("View collection unavailable for channel %s: %s",channel_id,type(exc).__name__)


async def check(client, username, message_id):
    peer = await client.get_input_entity(username if username.startswith("@") else "@" + username)
    result = await client(functions.channels.GetMessagesRequest(
        channel=peer, id=[types.InputMessageID(id=int(message_id))]))
    msg = next((m for m in result.messages if getattr(m, "id", None) == int(message_id)), None)
    if isinstance(msg, types.MessageEmpty):
        return "MISSING"
    if isinstance(msg, (types.Message, types.MessageService)):
        return "PRESENT"
    return "UNKNOWN"


async def main():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    if os.getenv("REKLIO_MTPROTO_MONITOR_ENABLED") != "true":
        LOG.info("Monitor disabled; set REKLIO_MTPROTO_MONITOR_ENABLED=true to enable")
        return
    for name in ("TELEGRAM_API_ID", "TELEGRAM_API_HASH", "BOT_TOKEN"):
        if not os.getenv(name):
            raise RuntimeError("Required secret is missing: " + name)
    parser = argparse.ArgumentParser()
    parser.add_argument("--once", action="store_true")
    args = parser.parse_args()
    client = TelegramClient(StringSession(), int(os.environ["TELEGRAM_API_ID"]),
                            os.environ["TELEGRAM_API_HASH"])
    await client.start(bot_token=os.environ["BOT_TOKEN"])
    try:
        while True:
            try:
                db = pymysql.connect(**database_kwargs())
                try:
                    await collect_views(client,db)
                    for order_id, username, msg_id in candidates(db):
                        try:
                            status = await check(client, username, msg_id)
                            if status in ("PRESENT", "MISSING"):
                                record(db, order_id, status)
                        except (RPCError, ValueError, OSError, TimeoutError) as exc:
                            db.rollback()
                            LOG.warning("Unable to check order %s: %s", order_id, type(exc).__name__)
                finally:
                    db.close()
            except Exception:
                LOG.exception("MTProto monitor polling failed; no payout actions performed")
            if args.once:
                break
            await asyncio.sleep(INTERVAL)
    finally:
        await client.disconnect()


if __name__ == "__main__":
    asyncio.run(main())
