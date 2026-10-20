# Telegram Manager

Telegram bot for channel administrators, implemented in **C# / .NET 10**.

## Status

Early MVP skeleton: /start menu, placeholder sections for channel connection and advertising requests. No payments or database connection yet.

## Local or VPS launch

1. Create a bot using @BotFather.
2. Set the BOT_TOKEN environment variable (do not commit tokens).
3. Run `dotnet run --project src/TelegramManager.Bot`.

## Build without a computer

GitHub Actions builds and publishes the .NET project on every push. Go to **Actions → .NET build** to inspect results and download the build artifact.

## Roadmap

- Verify channel administrator rights before connecting a channel.
- PostgreSQL storage for users, channels, and advertising requests.
- Advertising booking and manual payment confirmation.
- Background scheduled publication.
- Paid subscriptions: later.

Never put secrets in repository files.
