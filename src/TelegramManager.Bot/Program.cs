using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("BOT_TOKEN environment variable is required.");
    return;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var bot = new TelegramBotClient(token);
var identity = await bot.GetMe(cancellation.Token);
Console.WriteLine($"Telegram Manager started: @{identity.Username}");

await bot.ReceiveAsync(
    async (client, update, ct) =>
    {
        if (update.Message is { Text: var text } message && text?.StartsWith("/start") == true)
        {
            await client.SendMessage(
                message.Chat.Id,
                "Telegram Manager — управление рекламой в каналах. Выберите раздел:",
                replyMarkup: Menu(),
                cancellationToken: ct);
            return;
        }

        if (update.CallbackQuery is not { } query) return;
        var response = query.Data switch
        {
            "ads" => "Рекламные заявки: модуль в разработке.",
            "connect" => "Подключение каналов: модуль в разработке. Пока не выдавайте боту права администратора.",
            "about" => "Telegram Manager на C# / .NET 10. Платежи пока отключены.",
            _ => "Telegram Manager — управление рекламой в каналах."
        };
        if (query.Message is { } callbackMessage)
            await client.EditMessageText(
                callbackMessage.Chat.Id, callbackMessage.MessageId, response,
                replyMarkup: query.Data == "home" ? Menu() : BackMenu(),
                cancellationToken: ct);
        await client.AnswerCallbackQuery(query.Id, cancellationToken: ct);
    },
    (client, exception, ct) =>
    {
        Console.Error.WriteLine(exception);
        return Task.CompletedTask;
    },
    new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
    cancellationToken: cancellation.Token);

static InlineKeyboardMarkup Menu() => new(new[]
{
    new[] { InlineKeyboardButton.WithCallbackData("📣 Рекламные заявки", "ads") },
    new[] { InlineKeyboardButton.WithCallbackData("➕ Подключить канал", "connect") },
    new[] { InlineKeyboardButton.WithCallbackData("ℹ️ О сервисе", "about") }
});

static InlineKeyboardMarkup BackMenu() => new(new[]
{
    new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "home") }
});
