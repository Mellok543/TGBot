using Telegram.Bot;
using Telegram.Bot.Types;

namespace TelegramManager.Bot;

public sealed class AdScheduler(Database database, ITelegramBotClient bot)
{
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var id in await database.DueAsync(ct))
                {
                    var ad = await database.ClaimAsync(id, ct);
                    if (ad is null) continue;
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
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"Publication #{id} failed: {ex}");
                        await database.FinishPublishAsync(id, null, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, ct);
                    }
                }
                foreach (var (id, channel, message) in await database.DueDeletionsAsync(ct))
                {
                    try
                    {
                        await bot.DeleteMessage(channel, message, ct);
                        await database.FinishDeletionAsync(id, null, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Console.Error.WriteLine($"Deletion #{id} failed: {ex}");
                        await database.FinishDeletionAsync(id, ex.Message.Length > 500 ? ex.Message[..500] : ex.Message, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Scheduler error: {ex}");
            }
            if (!await timer.WaitForNextTickAsync(ct)) break;
        }
    }
}
