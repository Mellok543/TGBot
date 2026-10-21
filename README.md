# Telegram Manager

C# / .NET 10 Telegram bot for Telegram channel owners.

## Current feature branch

- MySQL / MariaDB database with user and channel tables.
- Automatic user registration.
- Native Telegram channel picker with ownership verification.
- Bot permission validation and persistent channel listing.
- Advertising and payments are not yet implemented.

## Configuration

- `BOT_TOKEN` — Telegram Bot API token.
- `DATABASE_URL` — MySqlConnector connection string, for example:
  `Server=YOUR_DB_HOST;Port=3306;Database=telegram_manager;User ID=telegram_manager;Password=YOUR_SECRET;`

Never commit a real token or password. Set values in Pterodactyl environment variables.

## Run locally

```sh
dotnet run --project src/TelegramManager.Bot
```

The application creates its initial tables on startup. Database and application accounts should have only the permissions they need.

## Deployment

The production bot currently runs separately on Pterodactyl. Do not deploy this branch until its CI build succeeds and the MariaDB connection is configured. Updating GitHub does not automatically update the running bot.
