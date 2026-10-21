using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramManager.Bot;

public sealed class BotHandlers(Database database)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long ChannelId, string Field)> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _pendingAds = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _pendingSchedules = new();


    private async Task ShowBookingDaysAsync(ITelegramBotClient bot, long chatId, long requestId, CancellationToken ct)
    {
        var now = await database.GetDatabaseUtcAsync(ct);
        var rows = Enumerable.Range(0, 7).Select(i =>
        {
            var day = now.Add(DisplayOffset).Date.AddDays(i);
            return new[] { InlineKeyboardButton.WithCallbackData(day.ToString("ddd dd.MM", new System.Globalization.CultureInfo("ru-RU")), $"day:{requestId}:{day:yyyyMMdd}") };
        }).ToList();
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⚡ Ближайшее свободное время", $"nextslot:{requestId}") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("✍️ Ввести дату вручную", $"manualslot:{requestId}") });
        await bot.SendMessage(chatId, $"🕒 Сейчас: {now.Add(DisplayOffset):dd.MM.yyyy HH:mm:ss} (UTC+3)\nВыберите день публикации:", replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    private async Task ProposeBookingAsync(ITelegramBotClient bot, long chatId, long advertiser, long requestId, DateTime utc, CancellationToken ct)
    {
        var now = await database.GetDatabaseUtcAsync(ct);
        if (utc <= now.AddMinutes(2) || utc > now.AddDays(90))
        {
            await bot.SendMessage(chatId, "Время уже прошло или выходит за пределы 90 дней. Выберите другой слот.", cancellationToken: ct);
            return;
        }
        if (!await database.ProposeAsync(requestId, advertiser, utc, ct))
        {
            await bot.SendMessage(chatId, "Заявка недоступна для бронирования.", cancellationToken: ct);
            return;
        }
        await bot.SendMessage(chatId, $"Предложено {utc.Add(DisplayOffset):dd.MM.yyyy HH:mm} (UTC+3). Заявка отправлена владельцу на подтверждение.", cancellationToken: ct);
        var ad = await database.GetAdRequestAsync(requestId, ct);
        if (ad is null) return;
        try
        {
            await ShowAdAsync(bot, ad.Value.Owner, ad.Value.Owner, requestId, ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) { Console.Error.WriteLine(ex); }
    }

    private async Task ShowAdAsync(ITelegramBotClient bot, long chatId, long userId, long requestId, CancellationToken ct)
    {
        var ad = await database.GetAdRequestAsync(requestId, ct);
        if (ad is null || (ad.Value.Owner != userId && ad.Value.Advertiser != userId)) return;
        var a = ad.Value;
        await bot.SendMessage(chatId, $"Заявка #{a.Id} | {a.Channel}\nСтатус: {a.Status}\nЦена: {a.Price:0.##} ₽\nТип: {a.Type}\n{a.Text}", cancellationToken: ct);
        var status = await database.GetScheduleStatusAsync(a.Id, userId, ct);
        if (status is { } s && (s.Scheduled is not null || s.Error is not null || s.MessageId is not null))
            await bot.SendMessage(chatId, $"🕒 Публикация (UTC+3): {s.Scheduled?.Add(DisplayOffset):yyyy-MM-dd HH:mm}\nСтатус: {s.Status}\nОшибка: {s.Error ?? "нет"}\nID сообщения: {s.MessageId?.ToString() ?? "нет"}", cancellationToken: ct);
        if (a.Type == "photo" && a.File is not null)
            await bot.SendPhoto(chatId, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct);
        else if (a.Type == "video" && a.File is not null)
            await bot.SendVideo(chatId, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct);
        else if (a.Type == "document" && a.File is not null)
            await bot.SendDocument(chatId, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct);
        if (a.Advertiser == userId && a.Status == "Draft")
            await bot.SendMessage(chatId, "Выберите время размещения (UTC):", replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("📅 Предложить время", $"schedule:{a.Id}") }
            }), cancellationToken: ct);
        if (a.Owner == userId && a.Status == "Pending")
            await bot.SendMessage(chatId, "Подтвердите заявку и получение оплаты — реклама автоматически выйдет в указанное время.", replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✅ Подтвердить размещение", $"confirm:0:{a.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"decision:Rejected:{a.Id}") }
            }), cancellationToken: ct);
    }


    private static readonly (string Key, string Name)[] Categories =
    {
        ("news", "📰 Новости"), ("tech", "💻 Технологии"), ("games", "🎮 Игры"),
        ("business", "💼 Бизнес"), ("education", "📚 Образование"),
        ("entertainment", "🎭 Развлечения"), ("lifestyle", "🌿 Образ жизни"),
        ("sport", "⚽ Спорт"), ("other", "📦 Другое")
    };

    private static InlineKeyboardMarkup SettingsKeyboard(long id) => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("💰 Цена", $"edit:price:{id}"), InlineKeyboardButton.WithCallbackData("📝 Описание", $"edit:description:{id}") },
        new[] { InlineKeyboardButton.WithCallbackData("📦 Форматы", $"edit:formats:{id}") },
        new[] { InlineKeyboardButton.WithCallbackData("🏷 Категория", $"setcategory:{id}") },
        new[] { InlineKeyboardButton.WithCallbackData("🌐 В каталоге: вкл/выкл", $"listing:{id}") },
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
        var listed = await database.IsChannelListedAsync(channelId, userId, ct);
        var me = await bot.GetMe(ct);
        var directLink = $"https://t.me/{me.Username}?start=ad_{channelId}";
        var text = $"⚙️ {settings.Value.Title}\n\n💰 Цена рекламы: {settings.Value.Price:0.##} ₽\n📝 Описание: {(string.IsNullOrWhiteSpace(settings.Value.Description) ? "Не задано" : settings.Value.Description)}\n📦 Форматы: {settings.Value.Formats}\n🌐 Каталог: {(listed ? "Включён" : "Выключен")}\n\n🔗 Ссылка для заказа рекламы:\n{directLink}";
        if (messageId is int id)
            await bot.EditMessageText(chatId, id, text, replyMarkup: SettingsKeyboard(channelId), cancellationToken: ct);
        else
            await bot.SendMessage(chatId, text, replyMarkup: SettingsKeyboard(channelId), cancellationToken: ct);
    }

    private static readonly TimeSpan DisplayOffset = TimeSpan.FromHours(3);
    private static InlineKeyboardMarkup Home() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("📺 Мои каналы", "owner_home") },
        new[] { InlineKeyboardButton.WithCallbackData("🛒 Покупка рекламы", "buyer_home") },
        new[] { InlineKeyboardButton.WithCallbackData("📅 Мои размещения", "placements") },
        new[] { InlineKeyboardButton.WithCallbackData("👤 Мой профиль", "profile") },
        new[] { InlineKeyboardButton.WithCallbackData("⚙️ Настройки", "profile_settings") }
    });
    private static InlineKeyboardMarkup ForRole(string? role) => Home();
    private static InlineKeyboardMarkup OwnerHome() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("📺 Управление каналами", "channels") },
        new[] { InlineKeyboardButton.WithCallbackData("➕ Подключить канал", "connect") },
        new[] { InlineKeyboardButton.WithCallbackData("📥 Входящие заявки", "incoming") },
        new[] { InlineKeyboardButton.WithCallbackData("💰 Доходы", "owner_earnings") },
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") }
    });
    private static InlineKeyboardMarkup BuyerHome() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("⭐ Ранее заказанные каналы", "buyer_channels") },
        new[] { InlineKeyboardButton.WithCallbackData("📤 Мои заявки", "outgoing") },
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") }
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

            // ADMIN_TELEGRAM_ID must be a numeric Telegram user ID, never a username.
            var adminConfigured = long.TryParse(Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_ID"), out var adminId);
            if (message.Text?.StartsWith("/grantpro ", StringComparison.OrdinalIgnoreCase) == true ||
                message.Text?.StartsWith("/revokepro ", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!adminConfigured || user.Id != adminId)
                {
                    await bot.SendMessage(message.Chat.Id, "Недостаточно прав.", cancellationToken: ct);
                    return;
                }
                var args = message.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (args[0].Equals("/grantpro", StringComparison.OrdinalIgnoreCase) &&
                    args.Length == 3 && long.TryParse(args[1], out var target) &&
                    int.TryParse(args[2], out var days) && days is >= 1 and <= 3650)
                {
                    var granted = await database.GrantProAsync(target, days, ct);
                    await bot.SendMessage(message.Chat.Id, granted ? $"PRO выдан пользователю {target} на {days} дней." : "Пользователь не найден. Он должен сначала запустить бота.", cancellationToken: ct);
                    return;
                }
                if (args[0].Equals("/revokepro", StringComparison.OrdinalIgnoreCase) &&
                    args.Length == 2 && long.TryParse(args[1], out var revokeId))
                {
                    var revoked = await database.RevokeGrantedProAsync(revokeId, ct);
                    await bot.SendMessage(message.Chat.Id, revoked ? "Выданный вручную PRO отозван. Оплаченная подписка не затронута." : "Пользователь не найден.", cancellationToken: ct);
                    return;
                }
                await bot.SendMessage(message.Chat.Id, "Использование: /grantpro TELEGRAM_ID ДНИ или /revokepro TELEGRAM_ID", cancellationToken: ct);
                return;
            }
            if (message.Text?.StartsWith("/menu", StringComparison.Ordinal) == true)
            {
                await bot.SendMessage(message.Chat.Id, "Главное меню:", replyMarkup: Home(), cancellationToken: ct);
                return;
            }
            if (message.Text?.StartsWith("/cancel", StringComparison.Ordinal) == true)
            {
                _pending.TryRemove(user.Id, out _);
                _pendingAds.TryRemove(user.Id, out _);
                _pendingSchedules.TryRemove(user.Id, out _);
                await bot.SendMessage(message.Chat.Id, "Действие отменено.", replyMarkup: ForRole(await database.GetRoleAsync(user.Id, ct)), cancellationToken: ct);
                return;
            }

            if (_pendingSchedules.TryRemove(user.Id, out var scheduleId))
            {
                var input = message.Text?.Trim() ?? "";
                if (!DateTime.TryParseExact(input, "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var at) || at.Add(-DisplayOffset) <= DateTime.UtcNow.AddMinutes(2) || at.Add(-DisplayOffset) > DateTime.UtcNow.AddDays(90))
                {
                    await bot.SendMessage(message.Chat.Id, "Формат: 2026-11-15 14:30 (UTC+3). Время от 2 минут до 90 дней вперёд.", cancellationToken: ct);
                    return;
                }
                await ProposeBookingAsync(bot, message.Chat.Id, user.Id, scheduleId, at.Add(-DisplayOffset), ct);
                return;
            }
            if (_pendingAds.TryRemove(user.Id, out var adChannelId))
            {
                var type = message.Photo?.Length > 0 ? "photo" : message.Video is not null ? "video" : message.Document is not null ? "document" : message.Text is not null ? "text" : "";
                var body = message.Caption ?? message.Text;
                var file = type switch
                {
                    "photo" => message.Photo![^1].FileId,
                    "video" => message.Video!.FileId,
                    "document" => message.Document!.FileId,
                    _ => null
                };
                if (type == "" || (type == "text" && string.IsNullOrWhiteSpace(body)) || (body?.Length ?? 0) > 1000)
                {
                    await bot.SendMessage(message.Chat.Id, "Поддерживаются текст, фото, видео или документ. Подпись — до 1000 символов. Попробуйте заказать снова.", cancellationToken: ct);
                    return;
                }
                var requestId = await database.CreateAdRequestAsync(adChannelId, user.Id, type, body, file, ct);
                if (requestId is null)
                {
                    await bot.SendMessage(message.Chat.Id, "Не удалось создать заявку: канал недоступен или принадлежит вам.", cancellationToken: ct);
                    return;
                }
                await bot.SendMessage(message.Chat.Id, $"Рекламный материал сохранён (заявка #{requestId}). Теперь выберите дату и время публикации.", cancellationToken: ct);
                await ShowBookingDaysAsync(bot, message.Chat.Id, requestId.Value, ct);
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
            {
                var payload = message.Text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                if (payload.Length == 2 && payload[1].StartsWith("ad_") && long.TryParse(payload[1][3..], out var directId))
                {
                    await ShowOfferAsync(bot, message.Chat.Id, null, directId, ct);
                }
                else await bot.SendMessage(message.Chat.Id, "Telegram Manager\nГлавное меню:", replyMarkup: Home(), cancellationToken: ct);
            }
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
            case "owner_home":

                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "📺 Панель владельца каналов", replyMarkup: OwnerHome(), cancellationToken: ct);
                break;
            case "buyer_home":

                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "📣 Кабинет рекламодателя. Для заказа рекламы перейдите по ссылке, предоставленной каналом.", replyMarkup: BuyerHome(), cancellationToken: ct);
                break;
            case "home":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "Telegram Manager", replyMarkup: Home(), cancellationToken: ct);
                break;
            case "placements":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "📅 Мои размещения", replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("📥 Продажа рекламы", "incoming") },
                    new[] { InlineKeyboardButton.WithCallbackData("📤 Покупка рекламы", "outgoing") },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") }
                }), cancellationToken: ct);
                break;
            case "profile":
                var summary = await database.GetProfileSummaryAsync(actor.Id, ct);
                var profilePlan = await database.GetPlanAsync(actor.Id, ct);
                var profileText = $"👤 Профиль\nТариф: {(profilePlan.Pro ? "PRO" : "Бесплатный")}\nКаналов: {summary.Channels}/{profilePlan.Limit}\nЗаказов рекламы: {summary.Bought}\nПродаж рекламы: {summary.Sold}\nОпубликовано: {summary.Published}\n\nУчтено размещений: {summary.BookedRub:0.##} ₽ (не фактическая выручка)\nПодтверждённый доход: {summary.EarnedStars} ⭐";
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, profileText,
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("📊 Аналитика каналов · PRO", "channel_analytics") },
                        new[] { InlineKeyboardButton.WithCallbackData("🛒 Аналитика покупок", "buyer_analytics") },
                        new[] { InlineKeyboardButton.WithCallbackData("⭐ Мой тариф", "subscription_status") },
                        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") }
                    }), cancellationToken: ct);
                break;
            case "channel_analytics":
                var analyticsPlan = await database.GetPlanAsync(actor.Id, ct);
                if (!analyticsPlan.Pro)
                {
                    await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "📊 Детальная статистика по каждому каналу доступна в PRO (299 Stars / 30 дней).\nБесплатная сводка доступна в профиле.", replyMarkup: Back(), cancellationToken: ct);
                    break;
                }
                var channelAnalytics = await database.GetChannelAnalyticsAsync(actor.Id, ct);
                var channelAnalyticsText = channelAnalytics.Count == 0 ? "Пока нет подключённых каналов." :
                    "📊 Статистика каналов (всё время):\n\n" + string.Join("\n\n", channelAnalytics.Select(x => $"📺 {x.Channel}\nРазмещений: {x.Orders}\nСумма заказов: {x.BookedRub:0.##} ₽\nУчтённый доход: {x.EarnedStars} ⭐"));
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, channelAnalyticsText, replyMarkup: Back(), cancellationToken: ct);
                break;
            case "buyer_analytics":
                var buyerAnalytics = await database.GetBuyerAnalyticsAsync(actor.Id, ct);
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId,
                    $"🛒 Покупки рекламы\nПодтверждённых заказов: {buyerAnalytics.Bought}\nСумма заказов: {buyerAnalytics.BookedRub:0.##} ₽\n\nЭто стоимость заказов, не подтверждение оплаты.",
                    replyMarkup: Back(), cancellationToken: ct);
                break;
            case "subscription_status":
                var plan = await database.GetPlanAsync(actor.Id, ct);
                var planText = plan.Pro
                    ? $"⭐ PRO активен до {plan.ProEnds!.Value.Add(DisplayOffset):dd.MM.yyyy HH:mm} (UTC+3).\nЛимит: 10 каналов."
                    : plan.TrialEnds == DateTime.MaxValue
                        ? "🎁 Бесплатные 30 дней начнутся после подключения первого канала. Лимит: 1 канал."
                        : plan.TrialEnds > DateTime.UtcNow
                        ? $"🎁 Бесплатный период до {plan.TrialEnds.Add(DisplayOffset):dd.MM.yyyy HH:mm} (UTC+3).\nЛимит: 1 канал."
                        : "⏳ Бесплатные 30 дней закончились. Для новых продаж и подключения каналов необходим PRO — 299 Stars / 30 дней.\nОплата Stars пока не подключена.";
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, planText,
                    replyMarkup: Back(), cancellationToken: ct);
                break;
            case "owner_earnings":
                var earnings = await database.GetOwnerEarningsAsync(actor.Id, ct);
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId,
                    $"💰 Доходы (Stars)\nОжидают: {earnings.Pending}\nДоступны: {earnings.Available}\nВыплачены: {earnings.Paid}\n\nЭто внутренний учёт. Автоматические выплаты ещё не подключены.",
                    replyMarkup: Back(), cancellationToken: ct);
                break;
            case "clock":
                var dbNow = await database.GetDatabaseUtcAsync(ct);
                await bot.SendMessage(origin.Chat.Id, $"🕒 Время (UTC+3): {dbNow.Add(DisplayOffset):dd.MM.yyyy HH:mm:ss}\nВремя базы (UTC): {dbNow:dd.MM.yyyy HH:mm:ss}\nЕсли время заявки позже этих значений, публикация ещё не наступила.", cancellationToken: ct);
                break;
            case "profile_settings":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "⚙️ Настройки профиля", replyMarkup: new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🕒 Текущее время", "clock") },
                    new[] { InlineKeyboardButton.WithCallbackData("⭐ Подписка", "subscription_status") },
                    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "home") }
                }), cancellationToken: ct);
                break;
            case "change_role":
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "Главное меню:", replyMarkup: Home(), cancellationToken: ct);
                break;
            case "buyer_channels":
                var previous = await database.GetPreviouslyOrderedChannelsAsync(actor.Id, ct);
                var previousRows = previous.Select(x => new[] { InlineKeyboardButton.WithCallbackData($"📺 {x.Title} · {x.Price:0.##} ₽", $"offer:{x.Id}") }).ToList();
                previousRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "buyer_home") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, previous.Count == 0 ? "Вы ещё не заказывали рекламу. Откройте ссылку от владельца канала." : "⭐ Каналы, в которых вы заказывали рекламу:", replyMarkup: new InlineKeyboardMarkup(previousRows), cancellationToken: ct);
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
            case string scheduleData when scheduleData.StartsWith("schedule:") && long.TryParse(scheduleData[9..], out var scheduleId):
                var scheduling = await database.GetAdRequestAsync(scheduleId, ct);
                if (scheduling is null || scheduling.Value.Advertiser != actor.Id || scheduling.Value.Status != "Draft") break;
                await ShowBookingDaysAsync(bot, origin.Chat.Id, scheduleId, ct);
                break;
            case string nextData when nextData.StartsWith("nextslot:") && long.TryParse(nextData[9..], out var nextId):
                var nextAd = await database.GetAdRequestAsync(nextId, ct);
                if (nextAd is null || nextAd.Value.Advertiser != actor.Id || nextAd.Value.Status != "Draft") break;
                var nextTime = await database.FindNextFreeAsync(nextId, (await database.GetDatabaseUtcAsync(ct)).AddMinutes(5), ct);
                if (nextTime is null) await bot.SendMessage(origin.Chat.Id, "Свободных слотов на ближайшие 30 дней не найдено.", cancellationToken: ct);
                else await ProposeBookingAsync(bot, origin.Chat.Id, actor.Id, nextId, nextTime.Value, ct);
                break;
            case string manualData when manualData.StartsWith("manualslot:") && long.TryParse(manualData[11..], out var manualId):
                var manualAd = await database.GetAdRequestAsync(manualId, ct);
                if (manualAd is null || manualAd.Value.Advertiser != actor.Id || manualAd.Value.Status != "Draft") break;
                _pendingSchedules[actor.Id] = manualId;
                await bot.SendMessage(origin.Chat.Id, "Введите дату и время (UTC+3): ГГГГ-ММ-ДД ЧЧ:ММ. Отмена: /cancel.", cancellationToken: ct);
                break;
            case string dayData when dayData.StartsWith("day:"):
                var dayParts = data.Split(':');
                if (dayParts.Length != 3 || !long.TryParse(dayParts[1], out var dayId) ||
                    !DateTime.TryParseExact(dayParts[2], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var chosenDay)) break;
                var dayAd = await database.GetAdRequestAsync(dayId, ct);
                if (dayAd is null || dayAd.Value.Advertiser != actor.Id || dayAd.Value.Status != "Draft") break;
                var utcNow = await database.GetDatabaseUtcAsync(ct);
                if (chosenDay.Date < utcNow.Add(DisplayOffset).Date || chosenDay.Date > utcNow.Add(DisplayOffset).Date.AddDays(7)) break;
                var timeRows = new List<InlineKeyboardButton[]>();
                foreach (var hour in new[] { 9, 12, 15, 18, 21 })
                {
                    var time = chosenDay.Date.AddHours(hour);
                    if (time.Add(-DisplayOffset) <= utcNow.AddMinutes(2)) continue;
                    timeRows.Add(new[] { InlineKeyboardButton.WithCallbackData($"{hour:00}:00 (UTC+3)", $"slot:{dayId}:{time:yyyyMMddHHmm}") });
                }
                timeRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Другой день", $"schedule:{dayId}") });
                await bot.SendMessage(origin.Chat.Id, $"📅 {chosenDay:dd.MM.yyyy}. Выберите время (UTC+3):", replyMarkup: new InlineKeyboardMarkup(timeRows), cancellationToken: ct);
                break;
            case string slotData when slotData.StartsWith("slot:"):
                var slotParts = data.Split(':');
                if (slotParts.Length != 3 || !long.TryParse(slotParts[1], out var slotId) ||
                    !DateTime.TryParseExact(slotParts[2], "yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var slotAt)) break;
                var slotAd = await database.GetAdRequestAsync(slotId, ct);
                if (slotAd is null || slotAd.Value.Advertiser != actor.Id || slotAd.Value.Status != "Draft") break;
                await ProposeBookingAsync(bot, origin.Chat.Id, actor.Id, slotId, slotAt.Add(-DisplayOffset), ct);
                break;
            case string confirmData when confirmData.StartsWith("confirm:"):
                var confirmParts = data.Split(':');
                if (confirmParts.Length != 3 || !long.TryParse(confirmParts[2], out var confirmId) || !int.TryParse(confirmParts[1], out var hours) || hours != 0) break;
                if (await database.ConfirmAsync(confirmId, actor.Id, null, ct))
                    {
                    await bot.EditMessageText(origin.Chat.Id, origin.MessageId, $"Заявка #{confirmId}: оплата подтверждена, публикация запланирована.", cancellationToken: ct);
                    var confirmed = await database.GetAdRequestAsync(confirmId, ct);
                    if (confirmed is not null)
                        try { await bot.SendMessage(confirmed.Value.Advertiser, $"✅ Заявка #{confirmId}: владелец подтвердил оплату. Реклама поставлена в очередь автоматической публикации.", cancellationToken: ct); }
                        catch (Telegram.Bot.Exceptions.ApiRequestException ex) { Console.Error.WriteLine(ex.Message); }
                }
                else await bot.SendMessage(origin.Chat.Id, "Не удалось подтвердить: заявка недоступна или время уже занято.", cancellationToken: ct);
                break;
            case "catalog":
                var categoryRows = Categories.Select(x => new[] { InlineKeyboardButton.WithCallbackData(x.Name, $"browsecategory:{x.Key}") }).ToList();
                categoryRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "home") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "📣 Выберите категорию каналов:", replyMarkup: new InlineKeyboardMarkup(categoryRows), cancellationToken: ct);
                break;
            case string categoryData when categoryData.StartsWith("browsecategory:"):
                var browseKey = categoryData["browsecategory:".Length..];
                if (!Categories.Any(x => x.Key == browseKey)) break;
                var categoryChannels = await database.GetCategoryChannelsAsync(browseKey, ct);
                var categoryChannelRows = categoryChannels.Select(x => new[] { InlineKeyboardButton.WithCallbackData($"📺 {x.Title} · {x.Price:0.##} ₽", $"offer:{x.Id}") }).ToList();
                categoryChannelRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Категории", "catalog") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, categoryChannels.Count == 0 ? "В этой категории пока нет каналов." : $"📣 {Categories.First(x => x.Key == browseKey).Name}:", replyMarkup: new InlineKeyboardMarkup(categoryChannelRows), cancellationToken: ct);
                break;
            case string setCategoryData when setCategoryData.StartsWith("setcategory:") && long.TryParse(setCategoryData["setcategory:".Length..], out var categoryChannelId):
                if (await database.GetChannelSettingsAsync(categoryChannelId, actor.Id, ct) is null) break;
                var chooseCategoryRows = Categories.Select(x => new[] { InlineKeyboardButton.WithCallbackData(x.Name, $"savecategory:{categoryChannelId}:{x.Key}") }).ToList();
                chooseCategoryRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"settings:{categoryChannelId}") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "🏷 Выберите категорию вашего канала:", replyMarkup: new InlineKeyboardMarkup(chooseCategoryRows), cancellationToken: ct);
                break;
            case string saveCategoryData when saveCategoryData.StartsWith("savecategory:"):
                var saveParts = data.Split(':');
                if (saveParts.Length != 3 || !long.TryParse(saveParts[1], out var saveChannelId)) break;
                if (!await database.SetChannelCategoryAsync(saveChannelId, actor.Id, saveParts[2], ct)) break;
                await ShowSettingsAsync(bot, origin.Chat.Id, origin.MessageId, actor.Id, saveChannelId, ct);
                break;
            case "incoming":
            case "outgoing":
                var owned = data == "incoming";
                var requests = await database.GetMyAdRequestsAsync(actor.Id, owned, ct);
                var requestRows = requests.Select(x => new[] { InlineKeyboardButton.WithCallbackData($"#{x.Id} · {x.Channel} · {x.Status}", $"ad:{x.Id}") }).ToList();
                requestRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "home") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, requests.Count == 0 ? "Заявок пока нет." : (owned ? "📥 Входящие заявки:" : "📤 Мои заявки:"), replyMarkup: new InlineKeyboardMarkup(requestRows), cancellationToken: ct);
                break;
            case string offerData when offerData.StartsWith("offer:") && long.TryParse(offerData[6..], out var offerId):
                await ShowOfferAsync(bot, origin.Chat.Id, origin.MessageId, offerId, ct);
                break;
            case string orderData when orderData.StartsWith("order:") && long.TryParse(orderData[6..], out var orderId):
                if (await database.GetPublicChannelAsync(orderId, ct) is null)
                {
                    await bot.SendMessage(origin.Chat.Id, "Канал больше не доступен.", cancellationToken: ct);
                    break;
                }
                _pendingAds[actor.Id] = orderId;
                _pending.TryRemove(actor.Id, out _);
                await bot.SendMessage(origin.Chat.Id, "Отправьте рекламный пост одним сообщением: текст, фото, видео или документ. Для отмены /cancel.", cancellationToken: ct);
                break;
            case string listingData when listingData.StartsWith("listing:") && long.TryParse(listingData[8..], out var listingId):
                if (await database.GetChannelSettingsAsync(listingId, actor.Id, ct) is null) break;
                var current = await database.IsChannelListedAsync(listingId, actor.Id, ct);
                await database.SetChannelListedAsync(listingId, actor.Id, !current, ct);
                await ShowSettingsAsync(bot, origin.Chat.Id, origin.MessageId, actor.Id, listingId, ct);
                break;
            case string adData when adData.StartsWith("ad:") && long.TryParse(adData[3..], out var adId):
                await ShowAdAsync(bot, origin.Chat.Id, actor.Id, adId, ct);
                break;
            case string decisionData when decisionData.StartsWith("decision:"):
                var decisionParts = data.Split(':');
                if (decisionParts.Length != 3 || decisionParts[1] != "Rejected" || !long.TryParse(decisionParts[2], out var decisionId)) break;
                if (await database.DecideAdRequestAsync(decisionId, actor.Id, "Rejected", ct))
                {
                    await bot.EditMessageText(origin.Chat.Id, origin.MessageId, $"Заявка #{decisionId} отклонена.", cancellationToken: ct);
                    var rejected = await database.GetAdRequestAsync(decisionId, ct);
                    if (rejected is not null)
                        try { await bot.SendMessage(rejected.Value.Advertiser, $"❌ Заявка #{decisionId} отклонена владельцем.", cancellationToken: ct); }
                        catch (Telegram.Bot.Exceptions.ApiRequestException ex) { Console.Error.WriteLine(ex.Message); }
                }
                else await bot.SendMessage(origin.Chat.Id, "Заявка уже обработана.", cancellationToken: ct);
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


    private async Task ShowOfferAsync(ITelegramBotClient bot, long chatId, int? messageId, long channelId, CancellationToken ct)
    {
        var offer = await database.GetPublicChannelAsync(channelId, ct);
        if (offer is null)
        {
            await bot.SendMessage(chatId, "Канал не найден или больше не подключён.", cancellationToken: ct);
            return;
        }
        var x = offer.Value;
        var text = $"📺 {x.Title}\n💰 Стоимость: {x.Price:0.##} ₽\n📝 {x.Description}\n📦 {x.Formats}\n\nОформите заявку на рекламу в этом канале.";
        var markup = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📝 Заказать рекламу", $"order:{channelId}") },
            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Меню", "buyer_home") }
        });
        if (messageId is int id) await bot.EditMessageText(chatId, id, text, replyMarkup: markup, cancellationToken: ct);
        else await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
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
