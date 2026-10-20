import asyncio
import logging

from aiogram import Bot, Dispatcher
from dotenv import load_dotenv
import os

from app.handlers import router


async def main() -> None:
    load_dotenv()
    token = os.getenv("BOT_TOKEN")
    if not token or token == "replace_with_your_bot_token":
        raise RuntimeError("Set BOT_TOKEN in .env or environment variables")

    logging.basicConfig(level=logging.INFO)
    bot = Bot(token=token)
    dispatcher = Dispatcher()
    dispatcher.include_router(router)
    try:
        await dispatcher.start_polling(bot)
    finally:
        await bot.session.close()


if __name__ == "__main__":
    asyncio.run(main())
