type TelegramWebApp = {
  initData: string;
  initDataUnsafe?: { start_param?: string; user?: { first_name?: string; username?: string } };
  colorScheme: string;
  ready(): void;
  expand(): void;
  openTelegramLink(url: string): void;
  openLink?(url: string): void;
};

declare global {
  interface Window { Telegram?: { WebApp?: TelegramWebApp } }
}

// Read on every use: the SDK loads asynchronously and may appear after this module does.
export const webApp = () => window.Telegram?.WebApp;

export function openChannelInTelegram(username: string) {
  const url = 'https://t.me/' + username.replace(/^@/, '');
  const app = webApp();
  if (app) app.openTelegramLink(url);
  else window.open(url, '_blank', 'noopener,noreferrer');
}

export function openExternal(url: string) {
  const app = webApp();
  if (app?.openLink) app.openLink(url);
  else window.open(url, '_blank', 'noopener,noreferrer');
}

/** The channel id from a buyer link such as t.me/ReklioBot?startapp=ad_123. */
export function startChannelId(): number | null {
  const query = new URLSearchParams(window.location.search);
  const raw = webApp()?.initDataUnsafe?.start_param || query.get('tgWebAppStartParam') || query.get('startapp') || '';
  const match = /^ad_(-?\d+)$/.exec(raw);
  return match ? Number(match[1]) : null;
}

export async function copyText(text: string): Promise<boolean> {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}
