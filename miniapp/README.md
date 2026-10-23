# Telegram Manager Mini App (React + TypeScript)

The Mini App is the main UI; the existing C# Telegram bot remains responsible for onboarding, channel connection and notifications during migration.

## Development

```sh
cd miniapp
npm install
npm run dev
npm run build
```

Configure `VITE_API_URL` to point at the HTTPS ASP.NET Core API. Set `BOT_TOKEN`, `DATABASE_URL` and `MINIAPP_ORIGIN` on the API server. Configure the Mini App HTTPS URL in BotFather. The API validates Telegram WebApp initData with HMAC and rejects stale or forged requests. Do not put BOT_TOKEN or database credentials in the frontend.

The initial release provides a real read-only catalog, user orders, owned channels, and a profile shell. Order creation, channel editing, statistics charts and YooKassa checkout remain in the existing bot until their secure API endpoints are implemented. Do not remove the bot's existing menus yet.
