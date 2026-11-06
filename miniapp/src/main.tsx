import React from 'react';
import { createRoot } from 'react-dom/client';
import { App } from './app/App';
import { trackSiteVisit } from './app/traffic';
import './style.css';

const root = createRoot(document.getElementById('root')!);

const telegramLaunch = window.location.pathname.startsWith('/app')
  || /tgWebApp(Data|Platform|StartParam)/.test(window.location.search + window.location.hash)
  || Boolean((window as Window & { TelegramWebviewProxy?: unknown }).TelegramWebviewProxy)
  || Boolean(window.Telegram?.WebApp?.initData);

let booted = false;
function boot() {
  if (booted) return;
  booted = true;
  if (telegramLaunch) {
    root.render(<React.StrictMode><App/></React.StrictMode>);
    return;
  }
  trackSiteVisit();
  import('./Landing')
    .then(({ default: Landing }) => root.render(<React.StrictMode><Landing/></React.StrictMode>))
    .catch(() => {
      const el = document.getElementById('root');
      if (el) el.textContent = 'Не удалось загрузить сайт. Обновите страницу.';
    });
}

// Telegram's SDK loads asynchronously so its CDN cannot freeze the public site.
if (!telegramLaunch || window.Telegram?.WebApp) boot();
else {
  window.addEventListener('reklio-telegram-ready', boot, { once: true });
  // A broken SDK should fail visibly, not leave a blank page.
  window.setTimeout(boot, 4500);
}
