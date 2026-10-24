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
await bot.SetMyCommands(new[]
{
    new Telegram.Bot.Types.BotCommand { Command = "menu", Description = "Открыть главное меню" },
    new Telegram.Bot.Types.BotCommand { Command = "start", Description = "Открыть Telegram Manager" },
    new Telegram.Bot.Types.BotCommand { Command = "cancel", Description = "Отменить действие" }
}, cancellationToken: cancellation.Token);
using var yooKassa = new YooKassaClient();
Console.WriteLine(yooKassa.Enabled ? "[Payments] YooKassa TEST checkout enabled." : "[Payments] YooKassa checkout disabled.");
var handlers = new BotHandlers(database, yooKassa);
var scheduler = new AdScheduler(database, bot);
var schedulerTask = scheduler.RunAsync(cancellation.Token);
Console.WriteLine($"Telegram Manager started: @{me.Username}");
await bot.ReceiveAsync(
    (client, update, ct) => handlers.HandleAsync(client, update, ct),
    (_, ex, _) => { Console.Error.WriteLine(ex); return Task.CompletedTask; },
    new ReceiverOptions { AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery, UpdateType.PreCheckoutQuery, UpdateType.ChannelPost] },
    cancellationToken: cancellation.Token);
await schedulerTask;
