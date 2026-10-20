# Telegram Manager

C# / .NET 10 Telegram bot for channel owners.

## Modules implemented (feature branch)

- User registration in PostgreSQL.
- Owner-only channel linking using Telegram's native channel picker.
- Checks that both owner and bot have suitable rights.
- Persistent list of linked channels.
- Advertising UI placeholder. Payments are not implemented.

## Requirements

- .NET 10 SDK/runtime.
- PostgreSQL with a separate database for this project.
- `BOT_TOKEN` environment variable.
- `DATABASE_URL` environment variable (Npgsql format, e.g. `Host=db.example;Port=5432;Database=telegram_manager;Username=telegram_manager;Password=YOUR_SECRET`).

Don't commit credentials or put them into chat messages. The bot automatically creates its initial database tables on startup; future changes should use migrations. Note: first startup requires database access and sufficient schema-creation permissions.

### Run

```sh
dotnet run --project src/TelegramManager.Bot
```

### Pterodactyl

Provide `DATABASE_URL` as a protected egg/server environment variable (alongside `BOT_TOKEN`). Do not reinstall the production server until database configuration is ready. Pterodactyl install runs only when installing or reinstalling; updating the GitHub repository does not update running files.

## Next steps

- Safer database migrations and audit trails.
- Channel disconnect/settings and permission revalidation.
- Advertising request workflows, scheduling and billing.
- Safe deployment from GitHub Actions after build validation.
