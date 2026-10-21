using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types.Enums;
using TelegramManager.Bot;

var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set BOT_TOKEN and DATABASE_URL environment variables.");

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };

await using var database = new Database(connectionString);
await database.InitializeAsync(cancellation.Token);

var bot = new TelegramBotClient(token);
var me = await bot.GetMe(cancellation.Token);
var handlers = new BotHandlers(database);
Console.WriteLine($"Telegram Manager started: @{me.Username}");
await bot.ReceiveAsync(
    (client, update, ct) => handlers.HandleAsync(client, update, ct),
    (_, ex, _) => { Console.Error.WriteLine(ex); return Task.CompletedTask; },
    new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery] },
    cancellationToken: cancellation.Token);
