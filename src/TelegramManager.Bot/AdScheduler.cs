using Telegram.Bot;
using Telegram.Bot.Types;

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
                foreach (var id in await database.DueAsync(ct))
                {
                    var ad = await database.ClaimAsync(id, ct);
                    if (ad is null) continue;
                    Console.WriteLine($"Publishing ad #{id} to channel {ad.Value.Channel} at {DateTime.UtcNow:O}");
                    try
                    {
                        var a = ad.Value;
                        Message posted = a.Type switch
                        {
                            "text" => await bot.SendMessage(a.Channel, a.Text ?? "", cancellationToken: ct),
                            "photo" when a.File is not null => await bot.SendPhoto(a.Channel, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct),
                            "video" when a.File is not null => await bot.SendVideo(a.Channel, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct),
                            "document" when a.File is not null => await bot.SendDocument(a.Channel, InputFile.FromFileId(a.File), caption: a.Text, cancellationToken: ct),
                            _ => throw new InvalidOperationException("Unsupported ad format")
                        };
                        await database.FinishPublishAsync(id, posted.MessageId, null, ct);
                        Console.WriteLine($"Ad #{id} published successfully, message #{posted.MessageId}");
                        await TryQueueNotificationAsync(id, $"✅ Заказ #{id}: реклама опубликована.", ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"Publication #{id} failed: {ex}");
                        await database.FinishPublishAsync(id, null, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, ct);
                        await database.EnqueueOrderNotificationAsync(id, $"❌ Заказ #{id}: публикация не удалась. Проверьте заказ.", ct);
                    }
                }
                foreach (var (id, channel, message) in await database.DueDeletionsAsync(ct))
                {
                    try
                    {
                        await bot.DeleteMessage(channel, message, ct);
                        await database.FinishDeletionAsync(id, null, ct);
                        await database.EnqueueOrderNotificationAsync(id, $"🗑 Заказ #{id}: срок размещения завершён, сообщение удалено.", ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"Deletion #{id} failed: {ex}");
                        await database.FinishDeletionAsync(id, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, ct);
                        await database.EnqueueOrderNotificationAsync(id, $"⚠️ Заказ #{id}: не удалось удалить публикацию. Требуется проверка.", ct);
                    }
                }
                await DispatchNotificationsAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Scheduler error: {ex}");
            }
            if (!await timer.WaitForNextTickAsync(ct)) break;
        }
    }

    private async Task DispatchNotificationsAsync(CancellationToken ct)
    {
        foreach (var item in await database.ClaimNotificationsAsync(ct))
        {
            if (!await database.TryClaimNotificationAsync(item.Id, ct)) continue;
            try
            {
                await bot.SendMessage(item.Recipient, item.Body, cancellationToken: ct);
                await database.CompleteNotificationAsync(item.Id, null, false, ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                var permanent = ex.ErrorCode is 400 or 403;
                await database.CompleteNotificationAsync(item.Id, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, permanent, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await database.CompleteNotificationAsync(item.Id, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, false, ct);
            }
        }
    }
    // Notification failures must not change the already-completed publication/deletion state.
    private async Task TryQueueNotificationAsync(long orderId, string message, CancellationToken ct)
    {
        try { await database.EnqueueOrderNotificationAsync(orderId, message, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"Unable to enqueue notification for order #{orderId}: {ex}");
        }
    }

}
