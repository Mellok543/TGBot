using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramManager.Bot;

public sealed class BotHandlers(Database database)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long ChannelId, string Field)> _pending = new();

    private static InlineKeyboardMarkup SettingsKeyboard(long id) => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("💰 Цена", $"edit:price:{id}"), InlineKeyboardButton.WithCallbackData("📝 Описание", $"edit:description:{id}") },
        new[] { InlineKeyboardButton.WithCallbackData("📦 Форматы", $"edit:formats:{id}") },
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Мои каналы", "channels") }
    });

    private async Task ShowSettingsAsync(ITelegramBotClient bot, long chatId, int? messageId, long userId, long channelId, CancellationToken ct)
    {
        var settings = await database.GetChannelSettingsAsync(channelId, userId, ct);
        if (settings is null)
        {
            await bot.SendMessage(chatId, "Канал не найден или у вас нет доступа.", cancellationToken: ct);
            return;
        }
        var text = $"⚙️ {settings.Value.Title}\n\n💰 Цена рекламы: {settings.Value.Price:0.##} ₽\n📝 Описание: {(string.IsNullOrWhiteSpace(settings.Value.Description) ? "Не задано" : settings.Value.Description)}\n📦 Форматы: {settings.Value.Formats}";
        if (messageId is int id)
            await bot.EditMessageText(chatId, id, text, replyMarkup: SettingsKeyboard(channelId), cancellationToken: ct);
        else
            await bot.SendMessage(chatId, text, replyMarkup: SettingsKeyboard(channelId), cancellationToken: ct);
    }

    private static InlineKeyboardMarkup Home() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("📺 Мои каналы", "channels") },
        new[] { InlineKeyboardButton.WithCallbackData("➕ Подключить канал", "connect") },
        new[] { InlineKeyboardButton.WithCallbackData("📣 Реклама", "ads") },
        new[] { InlineKeyboardButton.WithCallbackData("ℹ️ О сервисе", "about") }
    });
    private static InlineKeyboardMarkup Back() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") }
    });
    private static InlineKeyboardMarkup Connect() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("📺 Выбрать канал", "request_chat") },
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "home") }
    });

    public async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Message is { } message)
        {
            if (message.From is not { } user || message.Chat.Type != ChatType.Private) return;
            await database.RegisterAsync(user.Id, user.Username, user.FirstName + (user.LastName is null ? "" : " " + user.LastName), ct);

            if (message.Text?.StartsWith("/cancel", StringComparison.Ordinal) == true)
            {
                _pending.TryRemove(user.Id, out _);
                await bot.SendMessage(message.Chat.Id, "Действие отменено.", replyMarkup: Home(), cancellationToken: ct);
                return;
            }

            if (_pending.TryRemove(user.Id, out var edit))
            {
                var input = message.Text?.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    await bot.SendMessage(message.Chat.Id, "Отправьте текстовое значение. Изменение отменено.", cancellationToken: ct);
                    return;
                }
                object value;
                if (edit.Field == "price")
                {
                    if (!decimal.TryParse(input.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var price)
                        || price < 0 || price > 99999999.99m)
                    {
                        await bot.SendMessage(message.Chat.Id, "Некорректная цена. Укажите число от 0 до 99999999,99. Попробуйте снова через настройки.", cancellationToken: ct);
                        return;
                    }
                    value = price;
                }
                else
                {
                    var maxLength = edit.Field == "description" ? 1000 : 255;
                    if (input.Length > maxLength)
                    {
                        await bot.SendMessage(message.Chat.Id, $"Слишком длинный текст (максимум {maxLength} символов).", cancellationToken: ct);
                        return;
                    }
                    value = input;
                }
                if (await database.UpdateChannelSettingAsync(edit.ChannelId, user.Id, edit.Field, value, ct))
                    await ShowSettingsAsync(bot, message.Chat.Id, null, user.Id, edit.ChannelId, ct);
                else
                    await bot.SendMessage(message.Chat.Id, "Нет доступа к каналу.", cancellationToken: ct);
                return;
            }

            if (message.ChatShared is { } shared)
            {
                await LinkChannelAsync(bot, user.Id, message.Chat.Id, shared.ChatId, ct);
                return;
            }

            if (message.Text?.StartsWith("/start", StringComparison.Ordinal) == true)
                await bot.SendMessage(message.Chat.Id, "Telegram Manager\nУправление вашими Telegram-каналами.", replyMarkup: Home(), cancellationToken: ct);
            else if (message.Text?.StartsWith("/cancel", StringComparison.Ordinal) == true)
                await bot.SendMessage(message.Chat.Id, "Действие отменено.", replyMarkup: Home(), cancellationToken: ct);
            return;
        }

        if (update.CallbackQuery is not { } query || query.From is not { } actor || query.Message is not { } origin) return;
        if (origin.Chat.Type != ChatType.Private)
        {
            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
            return;
        }
        await database.RegisterAsync(actor.Id, actor.Username, actor.FirstName + (actor.LastName is null ? "" : " " + actor.LastName), ct);

        var data = query.Data ?? "";
        switch (data)
        {
            case "home":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "Telegram Manager\nУправление вашими Telegram-каналами.", replyMarkup: Home(), cancellationToken: ct);
                break;
            case "connect":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId,
                    "Подключение канала\n\n1. Добавьте бота в администраторы канала с правом публикации.\n2. Нажмите «Выбрать канал».\n3. Подтвердите выбор.\n\nБот проверит ваши права и свои разрешения.",
                    replyMarkup: Connect(), cancellationToken: ct);
                break;
            case "request_chat":
                var keyboard = new ReplyKeyboardMarkup(new[]
                {
                    new KeyboardButton("📺 Выбрать свой канал")
                    {
                        RequestChat = new KeyboardButtonRequestChat
                        {
                            RequestId = 1,
                            ChatIsChannel = true,
                            ChatIsCreated = true,
                            BotIsMember = true,
                            UserAdministratorRights = new ChatAdministratorRights
                            {
                                CanManageChat = true, CanPostMessages = true,
                                CanDeleteMessages = false, CanManageVideoChats = false,
                                CanRestrictMembers = false, CanPromoteMembers = false,
                                CanChangeInfo = false, CanInviteUsers = false,
                                IsAnonymous = false, CanEditMessages = false, CanPinMessages = false,
                                CanManageTopics = false, CanPostStories = false,
                                CanEditStories = false, CanDeleteStories = false
                            }
                        }
                    }
                })
                { ResizeKeyboard = true, OneTimeKeyboard = true };
                await bot.SendMessage(origin.Chat.Id, "Нажмите кнопку ниже и выберите канал, где вы владелец. Для отмены используйте /cancel.", replyMarkup: keyboard, cancellationToken: ct);
                break;
            case "channels":
                var channels = await database.GetChannelsAsync(actor.Id, ct);
                var text = channels.Count == 0 ? "Пока нет подключённых каналов." : "📺 Выберите канал для настройки:";
                var rows = channels.Select(c => new[] { InlineKeyboardButton.WithCallbackData(c.Title.Length > 50 ? c.Title[..50] : c.Title, $"settings:{c.Id}") }).ToList();
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, text, replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
                break;
            case string settingsData when settingsData.StartsWith("settings:") && long.TryParse(settingsData["settings:".Length..], out _):
                await ShowSettingsAsync(bot, origin.Chat.Id, origin.MessageId, actor.Id, long.Parse(data["settings:".Length..]), ct);
                break;
            case string editData when editData.StartsWith("edit:"):
                var parts = data.Split(':');
                if (parts.Length != 3 || !long.TryParse(parts[2], out var editChannelId) || parts[1] is not ("price" or "description" or "formats"))
                    break;
                if (await database.GetChannelSettingsAsync(editChannelId, actor.Id, ct) is null)
                    break;
                _pending[actor.Id] = (editChannelId, parts[1]);
                var prompt = parts[1] switch
                {
                    "price" => "Введите цену рекламы в рублях (например, 1500).",
                    "description" => "Введите описание канала (до 1000 символов).",
                    _ => "Введите доступные форматы рекламы (до 255 символов)."
                };
                await bot.SendMessage(origin.Chat.Id, prompt + "\nДля отмены: /cancel", cancellationToken: ct);
                break;
            case "ads":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "Рекламные заявки появятся в следующем этапе.", replyMarkup: Back(), cancellationToken: ct);
                break;
            case "about":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "Telegram Manager • .NET 10\nПодключение каналов и регистрация пользователей. Платежи не включены.", replyMarkup: Back(), cancellationToken: ct);
                break;
        }
        await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
    }

    private async Task LinkChannelAsync(ITelegramBotClient bot, long userId, long chatId, long channelId, CancellationToken ct)
    {
        try
        {
            var channel = await bot.GetChat(channelId, cancellationToken: ct);
            if (channel.Type != ChatType.Channel) throw new InvalidOperationException("Это не канал.");
            var owner = await bot.GetChatMember(channelId, userId, cancellationToken: ct);
            if (owner.Status != ChatMemberStatus.Creator)
                throw new InvalidOperationException("Подключить канал может только его владелец.");
            var botUser = await bot.GetMe(ct);
            var membership = await bot.GetChatMember(channelId, botUser.Id, cancellationToken: ct);
            if (membership is not ChatMemberAdministrator admin || !admin.CanPostMessages)
                throw new InvalidOperationException("Боту нужны права администратора с разрешением публиковать сообщения.");
            await database.SaveChannelAsync(channelId, channel.Title ?? channelId.ToString(), channel.Username, userId, ct);
            await bot.SendMessage(chatId, $"Канал «{channel.Title}» подключён.", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
            await bot.SendMessage(chatId, "Выберите раздел:", replyMarkup: Home(), cancellationToken: ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Telegram.Bot.Exceptions.ApiRequestException)
        {
            await bot.SendMessage(chatId, $"Не удалось подключить канал: {ex.Message}", replyMarkup: new ReplyKeyboardRemove(), cancellationToken: ct);
        }
    }
}
