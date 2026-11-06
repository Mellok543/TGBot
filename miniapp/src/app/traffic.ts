import { API, post } from './api';
import { webApp } from './telegram';

const SOURCE_KEY = 'reklio-source';
const VISIT_KEY = 'reklio-visit-counted';
const SOURCE = /^(tiktok|youtube|vk|telegram|instagram)(_\d{1,3})?$/;

/** tiktok or tiktok_2 from a link such as reklio.site/?src=tiktok_2. */
export const validSource = (raw: string | null | undefined) => raw && SOURCE.test(raw) ? raw : null;

/** The source from a Telegram link such as t.me/ReklioBot?startapp=src_tiktok. */
export const sourceFromStartParam = (raw: string | null | undefined) => validSource(raw?.startsWith('src_') ? raw.slice(4) : null);

/** Keeps the source when a site link leads into Telegram. Channel links stay as they are. */
export function telegramLinkWithSource(href: string, source: string): string {
  return /^https:\/\/t\.me\/ReklioBot(\?startapp)?$/.test(href) ? 'https://t.me/ReklioBot?startapp=src_' + source : href;
}

// Storage can be blocked in private windows and in-app browsers. Counting must never break the page.
function read(storage: () => Storage, key: string): string | null {
  try { return storage().getItem(key); } catch { return null; }
}
function write(storage: () => Storage, key: string, value: string) {
  try { storage().setItem(key, value); } catch { /* the source is simply not remembered */ }
}
const local = () => window.localStorage;
const session = () => window.sessionStorage;

export function trackSiteVisit() {
  const source = validSource(new URLSearchParams(window.location.search).get('src')) ?? validSource(read(local, SOURCE_KEY));
  if (source) {
    write(local, SOURCE_KEY, source);
    // The site has many Telegram links, so they are all handled here rather than one by one.
    document.addEventListener('click', event => {
      const link = (event.target as Element).closest?.('a');
      if (link) link.href = telegramLinkWithSource(link.href, source);
    }, true);
  }
  if (read(session, VISIT_KEY) || window.location.pathname.startsWith('/account')) return;
  write(session, VISIT_KEY, '1');
  void fetch(API + '/api/public/visit', {
    method: 'POST',
    keepalive: true,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ source: source ?? 'direct' })
  }).catch(() => {});
}

export function reportUserSource() {
  const query = new URLSearchParams(window.location.search);
  const source = sourceFromStartParam(webApp()?.initDataUnsafe?.start_param || query.get('tgWebAppStartParam')) ?? validSource(read(local, SOURCE_KEY));
  if (source) void post('/api/me/source', { source }).catch(() => {});
}
