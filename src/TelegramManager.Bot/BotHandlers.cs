using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Payments;

namespace TelegramManager.Bot;

public sealed class BotHandlers(Database database)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, (long ChannelId, string Field)> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _pendingAds = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _pendingCatalogSearch = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _catalogSearch = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, decimal> _catalogMaxPrice = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _pendingSchedules = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long,long> _pendingTimeRates = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long,(long OrderId,long RateId)> _pendingDurationSelections = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, string> _adminPending = new();
    private static bool IsAdmin(long id) => long.TryParse(Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_ID"), out var admin) && id == admin;
    private static InlineKeyboardMarkup AdminMenu() => new(new[]
    {
        new[] { InlineKeyboardButton.WithCallbackData("⭐ Выдать PRO", "admin:grant") },
        new[] { InlineKeyboardButton.WithCallbackData("🚫 Отозвать выданный PRO", "admin:revoke") },
        new[] { InlineKeyboardButton.WithCallbackData("🧪 Добавить тестовый аккаунт", "admin:teston") },
        new[] { InlineKeyboardButton.WithCallbackData("🧪 Убрать тестовый аккаунт", "admin:testoff") },
        new[] { InlineKeyboardButton.WithCallbackData("👥 Последние пользователи", "admin:users") },
        new[] { InlineKeyboardButton.WithCallbackData("⚖️ Спорные заказы", "admin:disputes") },
        new[] { InlineKeyboardButton.WithCallbackData("🩺 Состояние заказов и очереди", "admin:operations") },
        new[] { InlineKeyboardButton.WithCallbackData("📨 Недоставленные уведомления", "admin:failednotices") },
        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") }
    });


    private async Task ShowTimeRatesAsync(ITelegramBotClient bot,long chatId,int? messageId,
        long owner,long channelId,CancellationToken ct)
    {
        if(await database.GetChannelSettingsAsync(channelId,owner,ct) is null)return;
        var rates=await database.GetDurationRatesAsync(channelId,ct);
        var rows=rates.Select(x=>new[]
        {
            InlineKeyboardButton.WithCallbackData(
                $"❌ {x.Duration} ч — {x.Price:0.##} ₽",
                $"removerate:{channelId}:{x.Id}")
        }).ToList();
        rows.Add(new[]{InlineKeyboardButton.WithCallbackData("➕ Добавить вариант",$"addrate:{channelId}")});
        rows.Add(new[]{InlineKeyboardButton.WithCallbackData("⬅️ Настройки канала",$"settings:{channelId}")});
        var body="📦 Варианты размещения\nУкажите длительность публикации и стоимость. Например: 24 часа — 1600 ₽, 48 часов — 3000 ₽.\nНажмите на вариант, чтобы удалить его.\n"+
            (rates.Count==0?"Вариантов пока нет. Пока они не настроены, действует стандартный срок и общая цена.":$"Активных вариантов: {rates.Count}");
        if(messageId is int id)await bot.EditMessageText(chatId,id,body,replyMarkup:new InlineKeyboardMarkup(rows),cancellationToken:ct);
        else await bot.SendMessage(chatId,body,replyMarkup:new InlineKeyboardMarkup(rows),cancellationToken:ct);
    }

    private async Task ShowOrderScheduleOptionsAsync(ITelegramBotClient bot,long chatId,long advertiser,long orderId,CancellationToken ct)
    {
        var channelId=await database.GetOrderChannelAsync(orderId,advertiser,ct);
        if(channelId is null)return;
        var rates=await database.GetDurationRatesAsync(channelId.Value,ct);
        if(rates.Count==0)
        {
            await ShowBookingDaysAsync(bot,chatId,orderId,ct);
            return;
        }
        var rows=rates.Select(rate=>new[]
        {
            InlineKeyboardButton.WithCallbackData(
                $"📦 {rate.Duration} часов — {rate.Price:0.##} ₽",$"pickrate:{orderId}:{rate.Id}")
        }).ToList();
        await bot.SendMessage(chatId,"📦 Шаг 2/3: выберите срок размещения и цену. Затем выберите дату публикации.",
            replyMarkup:new InlineKeyboardMarkup(rows),cancellationToken:ct);
    }

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

    private async Task ProposeSelectedBookingAsync(ITelegramBotClient bot,long chatId,long advertiser,
        long orderId,DateTime utc,CancellationToken ct)
    {
        if(_pendingDurationSelections.TryGetValue(advertiser,out var selection) && selection.OrderId==orderId)
        {
            var ok=await database.ProposeDurationRateAsync(orderId,advertiser,selection.RateId,utc,ct);
            if(!ok)
            {
                await bot.SendMessage(chatId,"Не удалось забронировать: тариф изменён или время занято. Выберите вариант заново.",cancellationToken:ct);
                return;
            }
            _pendingDurationSelections.TryRemove(advertiser,out _);
            var order=await database.GetAdRequestAsync(orderId,ct);
            await bot.SendMessage(chatId,
                $"Заявка #{orderId}: {utc.Add(DisplayOffset):dd.MM.yyyy HH:mm} (UTC+3), {order?.Price:0.##} ₽. Отправлена владельцу на согласование.",
                cancellationToken:ct);
            if(order is not null)
                await database.EnqueueNotificationToUserAsync(orderId,order.Value.Owner,
                    $"📥 Новая заявка #{orderId} на рекламу. Откройте входящие заявки.",ct);
            return;
        }
        await ProposeBookingAsync(bot,chatId,advertiser,orderId,utc,ct);
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
        var detail = await database.GetAdOrderDetailsAsync(a.Id, userId, ct);
        var detailsText = detail is { } info
            ? $"\nСоздан: {info.Created.Add(DisplayOffset):dd.MM.yyyy HH:mm} (UTC+3)" +
              $"\nДата публикации: {(info.Scheduled is null ? "не выбрана" : info.Scheduled.Value.Add(DisplayOffset).ToString("dd.MM.yyyy HH:mm"))}" +
              $"\nСрок: {(info.DurationHours is null ? "не установлен" : $"{info.DurationHours} ч")}" +
              $"\nОплата: {info.Payment}{(info.Test ? " (тест, без списания)" : "")}" +
              $"\nВыплата: {info.Payout}" +
              $"\nОпубликовано: {(info.Published is null ? "нет" : info.Published.Value.Add(DisplayOffset).ToString("dd.MM.yyyy HH:mm"))}" +
              (info.Dispute is null ? "" : $"\nСпор: {info.Dispute}") +
              (info.Resolution is null ? "" : $"\nРешение: {info.Resolution}")
            : "";
        await bot.SendMessage(chatId, $"📋 Заказ #{a.Id} | {a.Channel}\nСтатус: {a.Status}\nЦена: {a.Price:0.##} ₽\nТип: {a.Type}\n{a.Text}{detailsText}", cancellationToken: ct);
        var history = await database.GetOrderHistoryAsync(a.Id, userId, ct);
        if (history.Count > 0)
            await bot.SendMessage(chatId, "📜 История заказа (UTC+3):\n" + string.Join("\n", history.Select(e => $"{e.At.Add(DisplayOffset):dd.MM HH:mm} · {e.Event}" + (e.Details is null ? "" : $" · {e.Details}"))), cancellationToken: ct);
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
        if ((a.Owner == userId || a.Advertiser == userId) && a.Status is "Published" or "Deleted" or "DeleteFailed")
            await bot.SendMessage(chatId, "⚖️ Обнаружили нарушение условий размещения? Откройте спор. Выплата будет заблокирована до проверки.",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("⚖️ Открыть спор", $"dispute:{a.Id}") } }), cancellationToken: ct);
        if(a.Advertiser==userId && a.Status is ("Published" or "Deleted"))
        {
            if(await database.CanReviewOrderAsync(a.Id,userId,ct))
                await bot.SendMessage(chatId,"⭐ Оцените сотрудничество с владельцем канала (1–5). Только для реальных оплаченных заказов.",
                    replyMarkup:new InlineKeyboardMarkup(new[]
                    {
                        Enumerable.Range(1,5).Select(n=>InlineKeyboardButton.WithCallbackData(
                            $"{n} ⭐",$"rate:{a.Id}:{n}")).ToArray()
                    }),cancellationToken:ct);
        }
        if (a.Advertiser == userId && a.Status == "AwaitingPayment")
            await bot.SendMessage(chatId, $"💳 Заказ #{a.Id}: к оплате {a.Price:0.##} ₽. Реальная оплата пока недоступна. Тестовая оплата разрешена только тестовым аккаунтам.",
                replyMarkup: new InlineKeyboardMarkup(new[] { new[] { InlineKeyboardButton.WithCallbackData("🧪 Тестовая оплата (без списания)", $"testpay:{a.Id}") } }), cancellationToken: ct);
        if (a.Owner == userId && a.Status == "Pending")
            await bot.SendMessage(chatId, "Подтвердите размещение. Рекламодатель затем должен оплатить заказ; подтверждение не считается оплатой.", replyMarkup: new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("✅ Подтвердить размещение", $"confirm:0:{a.Id}") },
                new[] { InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"decision:Rejected:{a.Id}") }
            }), cancellationToken: ct);
    }


    private readonly System.Collections.Concurrent.ConcurrentDictionary<long,long> _pendingReviewComments = new();

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
        new[] { InlineKeyboardButton.WithCallbackData("📦 Сроки и цены рекламы", $"timerates:{id}") },
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
    private static InlineKeyboardMarkup Home()
    {
        var rows = new List<InlineKeyboardButton[]>();
        var miniAppUrl = Environment.GetEnvironmentVariable("MINIAPP_URL");
        if (Uri.TryCreate(miniAppUrl,UriKind.Absolute,out var uri) && uri.Scheme==Uri.UriSchemeHttps)
            rows.Add(new[] { new InlineKeyboardButton("🚀 Открыть приложение")
            {
                WebApp = new WebAppInfo { Url = uri.ToString() }
            } });
        // Legacy bot controls remain available until all write workflows move to Mini App.
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("📺 Мои каналы", "owner_home") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🛒 Покупка рекламы", "buyer_home") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("📅 Мои размещения", "placements") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("👤 Мой профиль", "profile") });
        return new InlineKeyboardMarkup(rows);
    }
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
        new[] { InlineKeyboardButton.WithCallbackData("🔎 Каталог каналов", "catalog") },
        new[] { InlineKeyboardButton.WithCallbackData("❤️ Избранные каналы", "favorites") },
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

    private const int ProPriceStars = 299;
    private static string ProPayload(long userId) => $"pro30:{userId}";

    public async Task HandleAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if(update.ChannelPost is { } channelPost)
        {
            await database.ObserveChannelPostAsync(channelPost.Chat.Id,channelPost.MessageId,
                channelPost.Date.ToUniversalTime(),ct);
            return;
        }

        if (update.PreCheckoutQuery is { } pre)
        {
            var valid = pre.Currency == "XTR" && pre.TotalAmount == ProPriceStars &&
                        pre.InvoicePayload == ProPayload(pre.From.Id);
            await bot.AnswerPreCheckoutQuery(pre.Id, errorMessage: valid ? null : "Некорректный платёж. Создайте новый счёт в профиле.",
                cancellationToken: ct);
            return;
        }

        if (update.Message is { } reviewMessage && reviewMessage.Chat.Type == ChatType.Private &&
            reviewMessage.From is { } reviewAuthor &&
            _pendingReviewComments.ContainsKey(reviewAuthor.Id))
        {
            _pendingReviewComments.TryRemove(reviewAuthor.Id,out var pendingReviewId);
            if(reviewMessage.Text=="/cancel")
                await bot.SendMessage(reviewMessage.Chat.Id,"Отзыв отменён.",cancellationToken:ct);
            else if(reviewMessage.Text is not { } comment || comment.Length>500 || string.IsNullOrWhiteSpace(comment))
                await bot.SendMessage(reviewMessage.Chat.Id,"Комментарий должен содержать от 1 до 500 символов.",cancellationToken:ct);
            else if(await database.SaveReviewCommentAsync(pendingReviewId,reviewAuthor.Id,comment,ct))
                await bot.SendMessage(reviewMessage.Chat.Id,"Спасибо! Ваш отзыв сохранён.",cancellationToken:ct);
            else
                await bot.SendMessage(reviewMessage.Chat.Id,"Не удалось сохранить отзыв. Сначала поставьте оценку.",cancellationToken:ct);
            return;
        }

        if (update.Message is { } message)
        {
            if (message.From is not { } user || message.Chat.Type != ChatType.Private) return;
            await database.RegisterAsync(user.Id, user.Username, user.FirstName + (user.LastName is null ? "" : " " + user.LastName), ct);
            if (message.SuccessfulPayment is { } payment)
            {
                if (payment.Currency == "XTR" && payment.TotalAmount == ProPriceStars &&
                    payment.InvoicePayload == ProPayload(user.Id))
                {
                    var credited = await database.RecordSubscriptionPaymentAsync(user.Id,
                        payment.TelegramPaymentChargeId, payment.TotalAmount, ct);
                    var expiry = await database.GetSubscriptionUntilAsync(user.Id, ct);
                    await bot.SendMessage(message.Chat.Id,
                        credited ? $"⭐ PRO активирован до {expiry?.Add(DisplayOffset):dd.MM.yyyy HH:mm} (UTC+3)."
                                : "Платёж уже обработан или требует проверки. Если PRO не активен, обратитесь в поддержку.",
                        replyMarkup: Home(), cancellationToken: ct);
                }
                else
                {
                    Console.Error.WriteLine($"Unexpected payment payload for user {user.Id}; charge {payment.TelegramPaymentChargeId}");
                    await bot.SendMessage(message.Chat.Id, "Платёж получен, но данные заказа не совпали. Обратитесь в поддержку для проверки.", cancellationToken: ct);
                }
                return;
            }


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
            if (message.Text?.Equals("/admin", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!IsAdmin(user.Id)) { await bot.SendMessage(message.Chat.Id, "Недостаточно прав.", cancellationToken: ct); return; }
                await bot.SendMessage(message.Chat.Id, "🛡 Панель администратора", replyMarkup: AdminMenu(), cancellationToken: ct);
                return;
            }
            if (_adminPending.TryRemove(user.Id, out var adminAction))
            {
                if (!IsAdmin(user.Id)) return;
                var parts = (message.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || !long.TryParse(parts[0], out var targetId))
                {
                    await bot.SendMessage(message.Chat.Id, "Неверный Telegram ID. Откройте /admin и повторите.", cancellationToken: ct);
                    return;
                }
                bool ok;
                switch (adminAction)
                {
                    case "grant":
                        if (parts.Length != 2 || !int.TryParse(parts[1], out var days) || days is < 1 or > 3650)
                        {
                            await bot.SendMessage(message.Chat.Id, "Введите Telegram ID и срок в днях, например: 123456789 30", cancellationToken: ct);
                            return;
                        }
                        ok = await database.GrantProAsync(targetId, days, ct);
                        break;
                    case "revoke":
                        ok = await database.RevokeGrantedProAsync(targetId, ct);
                        break;
                    case "teston":
                    case "testoff":
                        ok = await database.SetTestAccountAsync(targetId, adminAction == "teston", ct);
                        break;
                    default: return;
                }
                await bot.SendMessage(message.Chat.Id,
                    ok ? "Изменения сохранены." : "Аккаунт не найден. Пользователь должен сначала открыть бота.",
                    replyMarkup: AdminMenu(), cancellationToken: ct);
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
                _pendingCatalogSearch.TryRemove(user.Id,out _);
                _pendingSchedules.TryRemove(user.Id, out _);
                _pendingTimeRates.TryRemove(user.Id, out _);
                _pendingDurationSelections.TryRemove(user.Id,out _);
                _adminPending.TryRemove(user.Id, out _);
                await bot.SendMessage(message.Chat.Id, "Действие отменено.", replyMarkup: ForRole(await database.GetRoleAsync(user.Id, ct)), cancellationToken: ct);
                return;
            }

            if (_pendingCatalogSearch.TryRemove(user.Id,out _))
            {
                var queryText=(message.Text??"").Trim();
                if(queryText.Length>80)
                {
                    await bot.SendMessage(message.Chat.Id,"Запрос слишком длинный (максимум 80 символов).",cancellationToken:ct);
                    return;
                }
                if(queryText=="/cancel" || queryText=="-") _catalogSearch.TryRemove(user.Id,out _);
                else if(queryText.Length>=2) _catalogSearch[user.Id]=queryText;
                else
                {
                    await bot.SendMessage(message.Chat.Id,"Введите хотя бы 2 символа или «-» для сброса.",cancellationToken:ct);
                    return;
                }
                await ShowCatalogPageAsync(bot,message.Chat.Id,null,"all","new",0,ct,user.Id);
                return;
            }
            if (_pendingTimeRates.TryRemove(user.Id,out var timeRateChannel))
            {
                var parts=(message.Text??"").Trim().Split(' ',StringSplitOptions.RemoveEmptyEntries);
                int duration=0;
                decimal price=0;
                var valid=parts.Length==2 &&
                    int.TryParse(parts[0],out duration) &&
                    decimal.TryParse(parts[1].Replace(',','.'),System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture,out price);
                if(!valid || !await database.SaveDurationRateAsync(timeRateChannel,user.Id,duration,price,ct))
                    await bot.SendMessage(message.Chat.Id,"Введите срок в часах и цену в рублях. Например: 24 1600. Допустимый срок: 1–720 часов.",cancellationToken:ct);
                else
                    await ShowTimeRatesAsync(bot,message.Chat.Id,null,user.Id,timeRateChannel,ct);
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
                await ProposeSelectedBookingAsync(bot, message.Chat.Id, user.Id, scheduleId, at.Add(-DisplayOffset), ct);
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
                await ShowOrderScheduleOptionsAsync(bot,message.Chat.Id,user.Id,requestId.Value,ct);
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
        // Acknowledge immediately: Telegram callback IDs expire while slow DB work runs.
        try
        {
            await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 400 &&
            (ex.Message.Contains("query is too old", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("query ID is invalid", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"Expired callback query ignored: {ex.Message}");
        }
        if (origin.Chat.Type != ChatType.Private) return;
        await database.RegisterAsync(actor.Id, actor.Username, actor.FirstName + (actor.LastName is null ? "" : " " + actor.LastName), ct);

        var data = query.Data ?? "";
        if (data.StartsWith("early:", StringComparison.Ordinal))
        {
            var bits=data.Split(':');
            if(bits.Length==3 && long.TryParse(bits[2],out var earlyOrderId)
               && bits[1] is ("republish" or "dispute"))
            {
                var republish=bits[1]=="republish";
                if(await database.ResolveEarlyDeletionAsync(earlyOrderId,actor.Id,republish,ct))
                {
                    await bot.EditMessageText(origin.Chat.Id,origin.MessageId,
                        republish
                          ? $"🔄 Заказ #{earlyOrderId}: повторная публикация поставлена в очередь. Бот разместит исходный рекламный материал заново."
                          : $"⚖️ Заказ #{earlyOrderId}: спор открыт. Решение доступно в Reklio.",
                        cancellationToken:ct);
                    var order=await database.GetAdRequestAsync(earlyOrderId,ct);
                    if(order is { } current)
                        await database.EnqueueNotificationToUserAsync(earlyOrderId,current.Advertiser,
                            republish
                              ? $"🔄 Заказ #{earlyOrderId}: владелец восстанавливает удалённую публикацию."
                              : $"⚖️ Заказ #{earlyOrderId}: владелец открыл спор из-за досрочного удаления.",ct);
                }
                else await bot.SendMessage(origin.Chat.Id,"Действие недоступно: заявка уже обработана или принадлежит другому владельцу.",cancellationToken:ct);
            }
            return;
        }
        if (data.StartsWith("admin:", StringComparison.Ordinal))
        {
            if (!IsAdmin(actor.Id))
            {
                await bot.SendMessage(origin.Chat.Id, "Недостаточно прав.", cancellationToken: ct);
                return;
            }
            var action = data["admin:".Length..];
            if (action == "back")
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "Администрирование", replyMarkup: AdminMenu(), cancellationToken: ct);
            else if (action.StartsWith("dispute:") && long.TryParse(action[8..], out var reviewId))
            {
                await bot.SendMessage(origin.Chat.Id, $"⚖️ Решение по спору #{reviewId}. Решение только фиксируется, денежные операции не выполняются.",
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("↩️ Рекомендовать возврат", $"admin:resolve:refund:{reviewId}") },
                        new[] { InlineKeyboardButton.WithCallbackData("✅ Рекомендовать выплату", $"admin:resolve:payout:{reviewId}") }
                    }), cancellationToken: ct);
            }
            else if (action.StartsWith("resolve:"))
            {
                var parts = action.Split(':');
                if (parts.Length == 3 && long.TryParse(parts[2], out var resolvedId))
                {
                    var decision = parts[1] switch { "refund" => "RefundRecommended", "payout" => "PayoutRecommended", _ => "" };
                    var success = await database.ResolveDisputeAsync(resolvedId, actor.Id, decision, ct);
                    await bot.SendMessage(origin.Chat.Id, success ? $"Решение по #{resolvedId} сохранено. Выплата остаётся заблокированной до обработки провайдером." : "Спор уже закрыт или решение недопустимо.", cancellationToken: ct);
                    if (success)
                    {
                        await database.EnqueueOrderNotificationAsync(resolvedId,
                            $"⚖️ По заказу #{resolvedId} вынесено решение: {decision}. Денежные операции не выполнялись.", ct);
                    }
                }
            }
            else if (action == "disputes")
            {
                var disputes = await database.GetOpenDisputesAsync(ct);
                var details = disputes.Count == 0 ? "Открытых споров нет." :
                    "⚖️ Открытые споры:\n" + string.Join("\n", disputes.Select(x => $"#{x.Id} · {x.Channel} · {x.Reason}"));
                var rows = disputes.Select(x => new[] { InlineKeyboardButton.WithCallbackData($"⚖️ Рассмотреть #{x.Id}", $"admin:dispute:{x.Id}") }).ToList();
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Админ-меню", "admin:back") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, details, replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
            }
            else if (action == "failednotices")
            {
                var failed = await database.GetFailedNotificationsAsync(ct);
                var lines = failed.Count == 0 ? "Нет недоставленных уведомлений." :
                    string.Join("\n", failed.Select(x => $"#{x.Id} · заказ {x.OrderId} · пользователь {x.Recipient} · попыток {x.Attempts}\n{x.Error ?? "Причина не указана"}"));
                var rows = failed.Select(x => new[]
                {
                    InlineKeyboardButton.WithCallbackData($"🔄 Повторить уведомление #{x.Id}", $"admin:retrynotice:{x.Id}")
                }).ToList();
                rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Админ-меню", "admin:back") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId,
                    "📨 Недоставленные уведомления (до 15)\n" + (lines.Length > 3400 ? lines[..3400] : lines),
                    replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
            }
            else if (action.StartsWith("retrynotice:") && long.TryParse(action["retrynotice:".Length..], out var noticeId))
            {
                var retried = await database.RetryFailedNotificationAsync(noticeId, ct);
                await bot.SendMessage(origin.Chat.Id, retried
                    ? $"Уведомление #{noticeId} поставлено на повторную отправку. Проверьте, что пользователь разблокировал бота."
                    : "Уведомление не найдено или уже обрабатывается.", cancellationToken: ct);
            }
            else if (action == "operations")
            {
                var stats = await database.GetOperationsSummaryAsync(ct);
                var problemOrders = await database.GetOrdersNeedingAttentionAsync(ct);
                var issues = problemOrders.Count == 0 ? "Нет заказов с ошибками." :
                    string.Join("\n", problemOrders.Select(x => $"#{x.Id} · {x.Status} · {x.Error ?? "Без подробностей"}"));
                var report = $"🩺 Состояние системы\nУведомления: ожидают {stats.Pending}, отправляются {stats.Sending}, ошибки {stats.Failed}\nЗависшие публикации: {stats.StuckPublishing}\nОшибки удаления: {stats.DeleteFailures}\n\nЗаказы для проверки:\n{issues}";
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId,
                    report.Length > 3900 ? report[..3900] : report,
                    replyMarkup: AdminMenu(), cancellationToken: ct);
            }
            else if (action == "users")
            {
                var users = await database.GetAdminUsersAsync(ct);
                var text = users.Count == 0 ? "Нет пользователей." :
                    "👥 Последние пользователи:\n" + string.Join("\n", users.Select(x => $"{x.Id} · {x.Name} {(x.Test ? "🧪" : "")}"));
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, text, replyMarkup: AdminMenu(), cancellationToken: ct);
            }
            else if (action is "grant" or "revoke" or "teston" or "testoff")
            {
                _adminPending[actor.Id] = action;
                var prompt = action == "grant" ? "Введите Telegram ID и количество дней через пробел (например: 123456789 30)." :
                    "Введите числовой Telegram ID пользователя.";
                await bot.SendMessage(origin.Chat.Id, prompt + "\n/cancel — отмена", cancellationToken: ct);
            }
            return;
        }
        switch (data)
        {
            case "owner_home":

                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "📺 Панель владельца каналов", replyMarkup: OwnerHome(), cancellationToken: ct);
                break;
            case "buyer_home":

                await bot.EditMessageText(origin.Chat.Id, origin.MessageId, "📣 Кабинет рекламодателя. Выберите канал в каталоге, сравните стоимость и опыт владельца, затем оформите заявку.", replyMarkup: BuyerHome(), cancellationToken: ct);
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
            case "buy_pro":
                await bot.SendInvoice(origin.Chat.Id,
                    title: "Telegram Manager PRO — 30 дней",
                    description: "До 10 каналов и расширенная статистика на 30 дней.",
                    payload: ProPayload(actor.Id),
                    currency: "XTR",
                    prices: new[] { new LabeledPrice("PRO — 30 дней", ProPriceStars) },
                    providerToken: "",
                    cancellationToken: ct);
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
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("⭐ Купить / продлить PRO — 299 Stars", "buy_pro") },
                        new[] { InlineKeyboardButton.WithCallbackData("⬅️ Главное меню", "home") }
                    }), cancellationToken: ct);
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
            case string rateListData when rateListData.StartsWith("timerates:") &&
                long.TryParse(rateListData["timerates:".Length..],out var rateChannel):
                await ShowTimeRatesAsync(bot,origin.Chat.Id,origin.MessageId,actor.Id,rateChannel,ct);
                break;
            case string addRateData when addRateData.StartsWith("addrate:") &&
                long.TryParse(addRateData["addrate:".Length..],out var addRateChannel):
                if(await database.GetChannelSettingsAsync(addRateChannel,actor.Id,ct) is null)break;
                _pendingTimeRates[actor.Id]=addRateChannel;
                await bot.SendMessage(origin.Chat.Id,
                    "📦 Новый вариант размещения. Введите срок в часах и цену в рублях через пробел.\nПример: 24 1600\nИли: 48 3000\n/cancel — отмена.",
                    cancellationToken:ct);
                break;
            case string removeRateData when removeRateData.StartsWith("removerate:"):
                var rateParts=removeRateData.Split(':');
                if(rateParts.Length!=3||!long.TryParse(rateParts[1],out var removeChannel)||
                    !long.TryParse(rateParts[2],out var removeId))break;
                await database.DeleteDurationRateAsync(removeChannel,actor.Id,removeId,ct);
                await ShowTimeRatesAsync(bot,origin.Chat.Id,origin.MessageId,actor.Id,removeChannel,ct);
                break;
            case string pickRateData when pickRateData.StartsWith("pickrate:"):
                var pickParts=pickRateData.Split(':');
                if(pickParts.Length!=3||!long.TryParse(pickParts[1],out var pickOrder)||
                    !long.TryParse(pickParts[2],out var pickRateId))break;
                var pickChannel=await database.GetOrderChannelAsync(pickOrder,actor.Id,ct);
                if(pickChannel is null)break;
                var selectedRates=await database.GetDurationRatesAsync(pickChannel.Value,ct);
                var selected=selectedRates.FirstOrDefault(x=>x.Id==pickRateId);
                if(selected.Id==0)break;
                _pendingDurationSelections[actor.Id]=(pickOrder,pickRateId);
                await ShowBookingDaysAsync(bot,origin.Chat.Id,pickOrder,ct);
                break;
            case string scheduleData when scheduleData.StartsWith("schedule:") && long.TryParse(scheduleData[9..], out var scheduleId):
                var scheduling = await database.GetAdRequestAsync(scheduleId, ct);
                if (scheduling is null || scheduling.Value.Advertiser != actor.Id || scheduling.Value.Status != "Draft") break;
                await ShowOrderScheduleOptionsAsync(bot,origin.Chat.Id,actor.Id,scheduleId,ct);
                break;
            case string nextData when nextData.StartsWith("nextslot:") && long.TryParse(nextData[9..], out var nextId):
                var nextAd = await database.GetAdRequestAsync(nextId, ct);
                if (nextAd is null || nextAd.Value.Advertiser != actor.Id || nextAd.Value.Status != "Draft") break;
                var nextTime = await database.FindNextFreeAsync(nextId, (await database.GetDatabaseUtcAsync(ct)).AddMinutes(5), ct);
                if (nextTime is null) await bot.SendMessage(origin.Chat.Id, "Свободных слотов на ближайшие 30 дней не найдено.", cancellationToken: ct);
                else await ProposeSelectedBookingAsync(bot, origin.Chat.Id, actor.Id, nextId, nextTime.Value, ct);
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
                await ProposeSelectedBookingAsync(bot, origin.Chat.Id, actor.Id, slotId, slotAt.Add(-DisplayOffset), ct);
                break;
            case string confirmData when confirmData.StartsWith("confirm:"):
                var confirmParts = data.Split(':');
                if (confirmParts.Length != 3 || !long.TryParse(confirmParts[2], out var confirmId) || !int.TryParse(confirmParts[1], out var hours) || hours != 0) break;
                if (await database.ConfirmAsync(confirmId, actor.Id, null, ct))
                    {
                    await database.RecordOrderEventAsync(confirmId, actor.Id, "PlacementApproved", "Ожидается оплата", ct);
                    await bot.EditMessageText(origin.Chat.Id, origin.MessageId, $"Заявка #{confirmId}: размещение согласовано, ожидается оплата.", cancellationToken: ct);
                    var confirmed = await database.GetAdRequestAsync(confirmId, ct);
                    if (confirmed is not null)
                        await database.EnqueueNotificationToUserAsync(confirmId, confirmed.Value.Advertiser,
                            $"✅ Заявка #{confirmId} одобрена!\nВладелец подтвердил размещение.\n💳 Следующий шаг — оплата в Reklio.", ct);
                }
                else await bot.SendMessage(origin.Chat.Id, "Не удалось подтвердить: заявка недоступна или время уже занято.", cancellationToken: ct);
                break;
            case "favorites":
                var favorites = await database.GetFavoriteChannelsAsync(actor.Id, ct);
                var favoriteRows = favorites.Select(x => new[]
                {
                    InlineKeyboardButton.WithCallbackData($"❤️ {x.Title} · {x.Price:0.##} ₽", $"offer:{x.Id}")
                }).ToList();
                favoriteRows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 Открыть каталог", "catalog") });
                favoriteRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Кабинет", "buyer_home") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId,
                    favorites.Count==0 ? "❤️ Избранное пока пусто. Добавляйте каналы из карточек каталога." :
                        $"❤️ Избранные каналы ({favorites.Count})", replyMarkup:new InlineKeyboardMarkup(favoriteRows), cancellationToken:ct);
                break;
            case string favoriteData when favoriteData.StartsWith("favorite:") &&
                long.TryParse(favoriteData["favorite:".Length..], out var favoriteId):
                await database.ToggleFavoriteAsync(actor.Id,favoriteId,ct);
                await ShowOfferAsync(bot,origin.Chat.Id,origin.MessageId,favoriteId,ct,actor.Id);
                break;
            case string reviewsData when reviewsData.StartsWith("reviews:") &&
                long.TryParse(reviewsData["reviews:".Length..],out var reviewsChannel):
                var reviews=await database.GetChannelReviewsAsync(reviewsChannel,ct);
                var reviewText=reviews.Count==0 ? "У канала пока нет проверенных отзывов." :
                    "⭐ Проверенные отзывы рекламодателей:\n\n"+
                    string.Join("\n\n",reviews.Select(x=>$"{new string('⭐',x.Stars)} · {x.At.Add(DisplayOffset):dd.MM.yyyy}\n{x.Comment??"Без комментария"}"));
                await bot.EditMessageText(origin.Chat.Id,origin.MessageId,reviewText,
                    replyMarkup:new InlineKeyboardMarkup(new[]
                    {
                        new[]{InlineKeyboardButton.WithCallbackData("⬅️ Карточка канала",$"offer:{reviewsChannel}")}
                    }),cancellationToken:ct);
                break;
            case string rateData when rateData.StartsWith("rate:"):
                var reviewRateParts=rateData.Split(':');
                if(reviewRateParts.Length!=3||!long.TryParse(reviewRateParts[1],out var rateOrder)||
                    !int.TryParse(reviewRateParts[2],out var stars)||stars<1||stars>5)break;
                if(await database.SaveReviewAsync(rateOrder,actor.Id,stars,ct))
                {
                    await bot.SendMessage(origin.Chat.Id,
                        $"⭐ Оценка {stars}/5 сохранена для заказа #{rateOrder}.\nМожете добавить комментарий (до 500 символов).",
                        replyMarkup:new InlineKeyboardMarkup(new[]
                        {
                            new[]{InlineKeyboardButton.WithCallbackData("✍️ Добавить комментарий",$"reviewcomment:{rateOrder}")},
                            new[]{InlineKeyboardButton.WithCallbackData("📋 Вернуться к заказу",$"ad:{rateOrder}")}
                        }),cancellationToken:ct);
                }
                else await bot.SendMessage(origin.Chat.Id,"Оценка доступна только рекламодателю после оплаченного реального размещения без открытого спора.",cancellationToken:ct);
                break;
            case string commentData when commentData.StartsWith("reviewcomment:") &&
                long.TryParse(commentData["reviewcomment:".Length..],out var commentOrder):
                if(!await database.CanReviewOrderAsync(commentOrder,actor.Id,ct))break;
                _pendingReviewComments[actor.Id]=commentOrder;
                await bot.SendMessage(origin.Chat.Id,"✍️ Отправьте текст отзыва (до 500 символов). /cancel — отмена.",cancellationToken:ct);
                break;
            case "catalogsearch":
                _pendingCatalogSearch[actor.Id]="search";
                await bot.SendMessage(origin.Chat.Id,"🔎 Введите часть названия канала (от 2 до 80 символов). Для сброса введите «-». /cancel — отмена.",cancellationToken:ct);
                break;
            case string priceFilterData when priceFilterData.StartsWith("catalogprice:"):
                if(priceFilterData=="catalogprice:clear") _catalogMaxPrice.TryRemove(actor.Id,out _);
                else if(decimal.TryParse(priceFilterData["catalogprice:".Length..],out var limit) &&
                    limit is 1000 or 3000 or 5000 or 10000) _catalogMaxPrice[actor.Id]=limit;
                else break;
                await ShowCatalogPageAsync(bot,origin.Chat.Id,origin.MessageId,"all","new",0,ct,actor.Id);
                break;
            case "catalog":
                var categoryRows = new List<InlineKeyboardButton[]>
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🌐 Все категории", "browse:all:new:0") }
                };
                categoryRows.AddRange(Categories.Select(x => new[] { InlineKeyboardButton.WithCallbackData(x.Name, $"browse:{x.Key}:new:0") }));
                categoryRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Кабинет рекламодателя", "buyer_home") });
                await bot.EditMessageText(origin.Chat.Id, origin.MessageId,
                    "🔎 Каталог рекламы\nВыберите тематику. Внутри доступны сортировка по цене и опыту владельца.",
                    replyMarkup: new InlineKeyboardMarkup(categoryRows), cancellationToken: ct);
                break;
            case string browseData when browseData.StartsWith("browse:"):
                var browseParts = browseData.Split(':');
                if (browseParts.Length != 4 || !int.TryParse(browseParts[3], out var browsePage)) break;
                if (browseParts[1] != "all" && !Categories.Any(x => x.Key == browseParts[1])) break;
                if (browseParts[2] is not ("new" or "price" or "expensive" or "rating")) break;
                await ShowCatalogPageAsync(bot, origin.Chat.Id, origin.MessageId,
                    browseParts[1], browseParts[2], browsePage, ct,actor.Id);
                break;
            case string categoryData when categoryData.StartsWith("browsecategory:"):
                var browseKey = categoryData["browsecategory:".Length..];
                if (!Categories.Any(x => x.Key == browseKey)) break;
                await ShowCatalogPageAsync(bot, origin.Chat.Id, origin.MessageId, browseKey, "new", 0, ct,actor.Id);
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
                await ShowOfferAsync(bot, origin.Chat.Id, origin.MessageId, offerId, ct, actor.Id);
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
            case string disputeData when disputeData.StartsWith("dispute:") && long.TryParse(disputeData[8..], out var disputedOrderId):
                await bot.SendMessage(origin.Chat.Id, $"Выберите причину спора по заказу #{disputedOrderId}:",
                    replyMarkup: new InlineKeyboardMarkup(new[]
                    {
                        new[] { InlineKeyboardButton.WithCallbackData("Публикация удалена досрочно", $"disputereason:deleted:{disputedOrderId}") },
                        new[] { InlineKeyboardButton.WithCallbackData("Нарушены условия размещения", $"disputereason:terms:{disputedOrderId}") }
                    }), cancellationToken: ct);
                break;
            case string reasonData when reasonData.StartsWith("disputereason:"):
                var reasonParts = reasonData.Split(':');
                if (reasonParts.Length != 3 || !long.TryParse(reasonParts[2], out var reasonOrderId)) break;
                var reason = reasonParts[1] switch
                {
                    "deleted" => "Реклама удалена до окончания срока",
                    "terms" => "Нарушены условия размещения",
                    _ => null
                };
                if (reason is null) break;
                if (await database.OpenDisputeAsync(reasonOrderId, actor.Id, reason, ct))
                {
                    await bot.SendMessage(origin.Chat.Id, $"⚖️ Спор по заказу #{reasonOrderId} открыт. Выплата заблокирована до проверки.", cancellationToken: ct);
                    var disputed = await database.GetAdRequestAsync(reasonOrderId, ct);
                    if (disputed is not null)
                    {
                        var other = disputed.Value.Owner == actor.Id ? disputed.Value.Advertiser : disputed.Value.Owner;
                        await database.EnqueueNotificationToUserAsync(reasonOrderId, other,
                            $"⚖️ По заказу #{reasonOrderId} открыт спор: {reason}. Выплата заблокирована.", ct);
                    }
                    if (long.TryParse(Environment.GetEnvironmentVariable("ADMIN_TELEGRAM_ID"), out var adminId))
                        await database.EnqueueNotificationToUserAsync(reasonOrderId, adminId,
                            $"⚖️ Новый спор #{reasonOrderId}: {reason}. Откройте /admin → Спорные заказы.", ct);
                }
                else await bot.SendMessage(origin.Chat.Id, "Не удалось открыть спор: заказ недоступен или спор уже открыт.", cancellationToken: ct);
                break;
            case string testPaymentData when testPaymentData.StartsWith("testpay:") && long.TryParse(testPaymentData[8..], out var testPaymentOrderId):
                if (await database.CompleteTestCheckoutAsync(testPaymentOrderId, actor.Id, ct))
                {
                    await database.RecordOrderEventAsync(testPaymentOrderId, actor.Id, "TestPayment", "Без списания денег", ct);
                    await bot.SendMessage(origin.Chat.Id, $"🧪 Заказ #{testPaymentOrderId}: тестовая оплата подтверждена. Реальные деньги не списаны. Публикация запланирована.", cancellationToken: ct);
                    var testOrder = await database.GetAdRequestAsync(testPaymentOrderId, ct);
                    if (testOrder is not null)
                        await database.EnqueueNotificationToUserAsync(testPaymentOrderId, testOrder.Value.Owner,
                            $"🧪 Тестовая оплата · заказ #{testPaymentOrderId}\n✅ Оплата имитирована, средства не списывались.\n📅 Ожидается публикация.", ct);
                }
                else await bot.SendMessage(origin.Chat.Id, "Тестовая оплата недоступна: проверьте статус заказа, время публикации и разрешение тестового аккаунта.", cancellationToken: ct);
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
                        await database.EnqueueNotificationToUserAsync(decisionId, rejected.Value.Advertiser,
                            $"❌ Заявка #{decisionId} отклонена\nВладелец не подтвердил размещение.\n🔎 Попробуйте выбрать другой канал в Reklio.", ct);
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
    }


    private async Task ShowCatalogPageAsync(ITelegramBotClient bot, long chatId, int? messageId,
        string category, string sort, int page, CancellationToken ct,long userId)
    {
        var search = _catalogSearch.TryGetValue(userId,out var searchText) ? searchText : null;
        decimal? maxPrice = _catalogMaxPrice.TryGetValue(userId,out var priceLimit) ? priceLimit : null;
        var count = await database.CountBrowseChannelsAsync(category, ct,search,maxPrice);
        var last = Math.Max(0, (count-1)/8);
        page = Math.Clamp(page, 0, Math.Min(last, 100));
        var channels = await database.BrowseChannelsAsync(category, sort, page, ct,search,maxPrice);
        var title = category == "all" ? "🌐 Все категории" : Categories.First(x => x.Key == category).Name;
        var rows = channels.Select(x => new[]
        {
            InlineKeyboardButton.WithCallbackData(
                $"📺 {x.Title} · {x.Price:0.##} ₽ · {x.Completed} размещ.", $"offer:{x.Id}")
        }).ToList();
        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🆕 Новые", $"browse:{category}:new:0"),
            InlineKeyboardButton.WithCallbackData("💰 Дешевле", $"browse:{category}:price:0")
        });
        rows.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("💎 Дороже", $"browse:{category}:expensive:0"),
            InlineKeyboardButton.WithCallbackData("🏅 Опыт", $"browse:{category}:rating:0")
        });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 Поиск по названию", "catalogsearch") });
        rows.Add(new[] {
            InlineKeyboardButton.WithCallbackData("≤ 1000 ₽","catalogprice:1000"),
            InlineKeyboardButton.WithCallbackData("≤ 3000 ₽","catalogprice:3000"),
            InlineKeyboardButton.WithCallbackData("≤ 5000 ₽","catalogprice:5000")
        });
        rows.Add(new[] {
            InlineKeyboardButton.WithCallbackData("≤ 10000 ₽","catalogprice:10000"),
            InlineKeyboardButton.WithCallbackData("Сбросить цену","catalogprice:clear")
        });
        var navigation = new List<InlineKeyboardButton>();
        if (page > 0) navigation.Add(InlineKeyboardButton.WithCallbackData("⬅️", $"browse:{category}:{sort}:{page-1}"));
        navigation.Add(InlineKeyboardButton.WithCallbackData($"{page+1}/{Math.Max(1,(count+7)/8)}", $"browse:{category}:{sort}:{page}"));
        if (page < last && page < 100) navigation.Add(InlineKeyboardButton.WithCallbackData("➡️", $"browse:{category}:{sort}:{page+1}"));
        rows.Add(navigation.ToArray());
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("🗂 Сменить категорию", "catalog") });
        rows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Кабинет", "buyer_home") });
        var sortLabel = sort switch { "price" => "дешевле", "expensive" => "дороже", "rating" => "по опыту", _ => "новые" };
        var filters=$"Поиск: {search??"все"} · Цена до: {(maxPrice is null?"любая":$"{maxPrice:0} ₽")}";
        var body = 
            $"🔎 {title}\n{filters}\nНайдено: {count} · Сортировка: {sortLabel}\n\n" +
            (channels.Count == 0 ? "Пока нет доступных каналов." :
             "Количество размещений — подтверждённые реальные заказы, не тестовые.");
        if(messageId is int mid) await bot.EditMessageText(chatId,mid,body,replyMarkup:new InlineKeyboardMarkup(rows),cancellationToken:ct);
        else await bot.SendMessage(chatId,body,replyMarkup:new InlineKeyboardMarkup(rows),cancellationToken:ct);
    }

    private async Task ShowOfferAsync(ITelegramBotClient bot, long chatId, int? messageId, long channelId, CancellationToken ct, long? viewerId = null)
    {
        var offer = await database.GetPublicChannelAsync(channelId, ct);
        if (offer is null)
        {
            await bot.SendMessage(chatId, "Канал не найден или больше не подключён.", cancellationToken: ct);
            return;
        }
        var x = offer.Value;
        var info = await database.GetChannelPublicInfoAsync(channelId, ct);
        var reputation = info is null ? null : await database.GetOwnerReputationAsync(info.Value.Owner, ct);
        var categoryName = info is null ? "Не указана" :
            Categories.FirstOrDefault(c => c.Key == info.Value.Category).Name ?? "Другое";
        string audience="👥 Подписчики: данные недоступны";
        try
        {
            using var metricsTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
            metricsTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            var members=await bot.GetChatMemberCount(channelId,cancellationToken:metricsTimeout.Token);
            audience=$"👥 Подписчики: {members:N0} (Telegram)";
            await database.SaveSubscriberSnapshotAsync(channelId,members,ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException)
        {
            // Bot may not have permission to inspect this channel.
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // Channel metrics must not block opening the offer.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Do not make users wait for unavailable channel statistics.
        }
        var metrics=await database.GetChannelMetricsAsync(channelId,ct);
        var trend=metrics is { Previous: int previous,Today: int today }
            ? $"📈 Изменение подписчиков за период от последнего замера не менее 7 дней назад: {today-previous:+#;-#;0}"
            : "📈 Динамика подписчиков: пока недостаточно замеров";
        var posting=metrics is { } m && m.DaysObserved>0
            ? $"📰 Зафиксировано публикаций: {m.Posts7} за 7 дней · {m.Posts30} за 30 дней"
            : "📰 Частота публикаций: наблюдение ещё не началось";
        var reviewSummary = await database.GetChannelReviewSummaryAsync(channelId,ct);
        var favorite = viewerId is long viewer && await database.IsFavoriteAsync(viewer,channelId,ct);
        var reviewLabel = reviewSummary.Count==0 ? "⭐ Отзывов пока нет" : $"⭐ {reviewSummary.Average:0.0}/5 · {reviewSummary.Count} проверенных оценок";
        var activity = reputation is { } rep
            ? $"🏅 Опыт владельца: {rep.Completed} подтверждённых размещений, {rep.RepeatClients} рекламодателей\n📊 Индекс активности: {rep.Rating:0}/100 (не отзывы)"
            : "🏅 Подтверждённых размещений пока нет";
        var text = $"📺 {x.Title}\n🏷 {categoryName}\n💰 Стоимость: {x.Price:0.##} ₽\n📝 {(string.IsNullOrWhiteSpace(x.Description) ? "Описание пока не добавлено" : x.Description)}\n📦 {x.Formats}\n{audience}\n{trend}\n{posting}\n\n{activity}\n\n{reviewLabel}\n\nОформите заявку на рекламу в этом канале.";
        var buttons = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("📝 Заказать рекламу", $"order:{channelId}") }
        };
        // Public channels can be opened directly. Private channels have no reliable
        // public URL unless the owner explicitly supplies an invitation link.
        if (info is { Username: { } username } &&
            System.Text.RegularExpressions.Regex.IsMatch(username, @"^@?[A-Za-z0-9_]{5,32}$"))
        {
            buttons.Add(new[]
            {
                InlineKeyboardButton.WithUrl("📢 Перейти в канал",
                    $"https://t.me/{username.TrimStart('@')}")
            });
        }
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData(favorite ? "💔 Убрать из избранного" : "❤️ В избранное", $"favorite:{channelId}") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("⭐ Читать отзывы", $"reviews:{channelId}") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🔎 Каталог", "catalog") });
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ Кабинет", "buyer_home") });
        var markup = new InlineKeyboardMarkup(buttons);
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
