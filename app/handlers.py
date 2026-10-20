from aiogram import Router
from aiogram.filters import CommandStart
from aiogram.types import Message, InlineKeyboardButton, InlineKeyboardMarkup, CallbackQuery
from aiogram import F

router = Router()


def home_keyboard() -> InlineKeyboardMarkup:
    return InlineKeyboardMarkup(inline_keyboard=[
        [InlineKeyboardButton(text="📣 Рекламные заявки", callback_data="ads")],
        [InlineKeyboardButton(text="➕ Подключить канал", callback_data="connect")],
        [InlineKeyboardButton(text="ℹ️ О сервисе", callback_data="about")],
    ])


@router.message(CommandStart())
async def start(message: Message) -> None:
    await message.answer("Telegram Manager — управление рекламой в каналах.\n\nВыберите раздел:", reply_markup=home_keyboard())


@router.callback_query(F.data.in_({"ads", "connect", "about"}))
async def navigate(query: CallbackQuery) -> None:
    messages = {
        "ads": "Раздел рекламных заявок появится в следующем этапе.",
        "connect": "Подключение каналов появится в следующем этапе. Пока не выдавайте боту права администратора.",
        "about": "Telegram Manager: автоматизация рекламы в Telegram. Платежи пока отключены.",
    }
    if query.message:
        await query.message.edit_text(messages[query.data], reply_markup=InlineKeyboardMarkup(inline_keyboard=[
            [InlineKeyboardButton(text="⬅️ Назад", callback_data="home")]
        ]))
    await query.answer()


@router.callback_query(F.data == "home")
async def back(query: CallbackQuery) -> None:
    if query.message:
        await query.message.edit_text("Telegram Manager — управление рекламой в каналах.\n\nВыберите раздел:", reply_markup=home_keyboard())
    await query.answer()
