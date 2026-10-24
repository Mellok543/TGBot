#!/usr/bin/env python3
"""Read-only MTProto probe for Reklio Bot. Never publishes, deletes or changes payouts."""
import argparse
import asyncio
import os
import sys

from telethon import TelegramClient, functions, types
from telethon.errors import RPCError
from telethon.sessions import StringSession


async def run(channel: str, message_id: int) -> int:
    api_id = os.getenv("TELEGRAM_API_ID")
    api_hash = os.getenv("TELEGRAM_API_HASH")
    bot_token = os.getenv("BOT_TOKEN")
    if not api_id or not api_hash or not bot_token:
        print("CONFIG_MISSING: TELEGRAM_API_ID, TELEGRAM_API_HASH and BOT_TOKEN are required")
        return 2

    # In-memory session: no .session file or auth key printed to logs.
    async with TelegramClient(StringSession(), int(api_id), api_hash) as client:
        await client.start(bot_token=bot_token)
        try:
            target = int(channel) if channel.lstrip("-").isdigit() else channel
            try:
                peer = await client.get_input_entity(target)
            except (ValueError, RPCError):
                # A channel's numeric ID alone is insufficient without its access hash.
                # Bots may not have access to dialogs; a public username is preferred for the probe.
                if isinstance(target, int):
                    print("PEER_NOT_RESOLVED: numeric channel ID needs cached access hash; test with a public @username first")
                    return 3
                raise
            result = await client(functions.channels.GetMessagesRequest(
                channel=peer, id=[types.InputMessageID(id=message_id)]
            ))
            matches = [item for item in result.messages if getattr(item, "id", None) == message_id]
            if not matches:
                print("NOT_FOUND: requested message was not returned")
                return 4
            item = matches[0]
            if isinstance(item, types.MessageEmpty):
                print("MISSING: Telegram returned MessageEmpty for the requested message")
                return 0
            if isinstance(item, (types.Message, types.MessageService)):
                print("PRESENT: Telegram returned the requested message")
                return 0
            print("INDETERMINATE: unexpected Telegram message type")
            return 5
        except RPCError as exc:
            print("TELEGRAM_ERROR:", type(exc).__name__)
            return 6


def main() -> int:
    parser = argparse.ArgumentParser(description="Read-only channel post visibility probe")
    parser.add_argument("--channel", required=True, help="Public @username (recommended) or known peer ID")
    parser.add_argument("--message-id", required=True, type=int)
    args = parser.parse_args()
    if args.message_id <= 0:
        parser.error("--message-id must be positive")
    return asyncio.run(run(args.channel, args.message_id))


if __name__ == "__main__":
    sys.exit(main())
