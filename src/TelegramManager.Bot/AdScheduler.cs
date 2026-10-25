using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace TelegramManager.Bot;

public sealed class AdScheduler(Database database, ITelegramBotClient bot)
{
    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("AdScheduler started; checking scheduled posts every 15 seconds (UTC).");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessPublicationsAsync(ct);
                await ProcessDeletionsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Scheduler processing error: {ex}");
            }

            // Delivery must continue even when publishing/deleting one order fails.
            try { await DispatchNotificationsAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Notification dispatcher error: {ex}");
            }

            if (!await timer.WaitForNextTickAsync(ct)) break;
        }
    }

    private async Task ProcessPublicationsAsync(CancellationToken ct)
    {
        foreach (var id in await database.DueAsync(ct))
        {
            try
            {
                var ad = await database.ClaimAsync(id, ct);
                if (ad is null) continue;
                Message? posted = null;
                string? sendError = null;
                try
                {
                    var a = ad.Value;
                    posted = a.Type switch
                    {
                        "text" => await bot.SendMessage(a.Channel, a.Text ?? "", cancellationToken: ct),
                        "photo" when a.File is not null => await bot.SendPhoto(a.Channel, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct),
                        "video" when a.File is not null => await bot.SendVideo(a.Channel, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct),
                        "document" when a.File is not null => await bot.SendDocument(a.Channel, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct),
                        _ => throw new InvalidOperationException("Unsupported ad format")
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sendError = Truncate(ex.Message);
                    Console.Error.WriteLine($"Publication #{id} failed: {ex}");
                }

                // Never reclassify an already sent Telegram post as a failed send because
                // a database write or audit insert failed afterwards.
                await database.FinishPublishAsync(id, posted?.MessageId, sendError, ct);
                await TryQueueNotificationAsync(id, posted is null
                    ? $"⚠️ Проблема с публикацией · заказ #{id}\nРеклама не была опубликована.\n📋 Откройте заказ в Reklio, чтобы узнать подробности."
                    : $"🎉 Реклама опубликована · заказ #{id}\n✅ Публикация успешно размещена в канале.\n📋 Следить за размещением можно в Reklio.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Publication processing #{id} failed: {ex}");
            }
        }
    }

    private async Task ProcessDeletionsAsync(CancellationToken ct)
    {
        foreach (var (id, channel, message) in await database.DueDeletionsAsync(ct))
        {
            try
            {
                string? deleteError = null;
                try { await bot.DeleteMessage(channel, message, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    deleteError = Truncate(ex.Message);
                    Console.Error.WriteLine($"Deletion #{id} failed: {ex}");
                }

                await database.FinishDeletionAsync(id, deleteError, ct);
                await TryQueueNotificationAsync(id, deleteError is null
                    ? $"🏁 Размещение завершено · заказ #{id}\n✅ Срок публикации истёк.\n🗑 Рекламный пост удалён по расписанию."
                    : $"⚠️ Требуется проверка · заказ #{id}\nСрок размещения истёк, но автоматически удалить пост не удалось.\n📋 Проверьте заказ в Reklio.", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Deletion processing #{id} failed: {ex}");
            }
        }
    }

    private async Task DispatchNotificationsAsync(CancellationToken ct)
    {
        foreach (var item in await database.ClaimNotificationsAsync(ct))
        {
            try
            {
                if (!await database.TryClaimNotificationAsync(item.Id, ct)) continue;
                try
                {
                    var notificationText = item.Body;
                    InlineKeyboardMarkup? actions = null;
                    if (item.OrderId > 0 && item.Body.StartsWith("Новая заявка", StringComparison.OrdinalIgnoreCase))
                    {
                        var order = await database.GetAdRequestAsync(item.OrderId, ct);
                        if (order is { } a && a.Owner == item.Recipient && a.Status is ("Pending" or "PendingPaid"))
                        {
                            var details = await database.GetAdOrderDetailsAsync(item.OrderId, item.Recipient, ct);
                            var preview = a.Text ?? "";
                            if (preview.Length > 650) preview = preview[..650] + "…";
                            var dateLabel = details?.Scheduled is { } date ? date.AddHours(3).ToString("dd.MM.yyyy HH:mm") + " (UTC+3)" : "не выбрана";
                            notificationText = $"📥 Новая заявка #{item.OrderId} на рекламу\n" +
                                $"📺 Канал: {a.Channel}\n💰 Стоимость: {a.Price:0.##} ₽\n" +
                                $"📅 Публикация: {dateLabel}\n" +
                                $"⏳ Срок: {details?.DurationHours?.ToString() ?? "—"} ч\n\n" +
                                $"📝 Рекламный текст:\n{preview}\n\nВыберите действие:";
                            actions = new InlineKeyboardMarkup(new[]
                            {
                                new[] { InlineKeyboardButton.WithCallbackData("✅ Одобрить", $"confirm:0:{item.OrderId}") },
                                new[] { InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"decision:Rejected:{item.OrderId}") }
                            });
                        }
                    }
                    await bot.SendMessage(item.Recipient, notificationText, replyMarkup: actions, cancellationToken: ct);
                    await database.CompleteNotificationAsync(item.Id, null, false, ct);
                }
                catch (Telegram.Bot.Exceptions.ApiRequestException ex)
                {
                    await database.CompleteNotificationAsync(item.Id, Truncate(ex.Message), ex.ErrorCode is 400 or 403, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await database.CompleteNotificationAsync(item.Id, Truncate(ex.Message), false, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Notification #{item.Id} processing failed: {ex}");
            }
        }
    }

    private async Task TryQueueNotificationAsync(long orderId, string message, CancellationToken ct)
    {
        try { await database.EnqueueOrderNotificationAsync(orderId, message, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Unable to enqueue notification for order #{orderId}: {ex}");
        }
    }

    private static string Truncate(string message) => message.Length > 500 ? message[..500] : message;
}
