import { webApp } from './telegram';
import type { Channel } from './types';

export const API = import.meta.env.VITE_API_URL || '';
const REQUEST_TIMEOUT_MS = 12000;
const UPLOAD_TIMEOUT_MS = 60000;

const authHeaders = () => ({ 'X-Telegram-Init-Data': webApp()?.initData || '' });

async function errorMessage(response: Response): Promise<string> {
  if (response.status === 401) return 'Сессия устарела. Закройте Reklio и откройте заново из Telegram.';
  if (response.status === 429) return 'Слишком много запросов. Подождите минуту и попробуйте снова.';
  const detail = await response.json().catch(() => ({})) as { error?: string };
  return detail.error || 'Сервер не смог выполнить запрос. Попробуйте ещё раз.';
}

// credentials: the website signs in with a cookie, the Mini App with the Telegram header above.
function send(path: string, method: string, body?: unknown): Promise<Response> {
  return fetch(API + path, {
    method,
    credentials: 'include',
    signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    headers: body === undefined ? authHeaders() : { 'Content-Type': 'application/json', ...authHeaders() },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
}

async function write<T>(path: string, method: string, body: unknown): Promise<T> {
  let response: Response;
  try {
    response = await send(path, method, body);
  } catch {
    throw new Error('Нет связи с сервером. Проверьте интернет и попробуйте снова. Если действие важное, сначала обновите список: оно могло уже выполниться.');
  }
  if (!response.ok) throw new Error(await errorMessage(response));
  return response.json() as Promise<T>;
}

export const post = <T = unknown>(path: string, body: unknown = {}) => write<T>(path, 'POST', body);
export const put = <T = unknown>(path: string, body: unknown) => write<T>(path, 'PUT', body);

export async function remove(path: string): Promise<void> {
  const response = await send(path, 'DELETE');
  if (!response.ok) throw new Error(await errorMessage(response));
}

// Reads are safe to repeat, so a dropped connection gets one more attempt.
export async function get<T>(path: string): Promise<T> {
  for (let attempt = 0; ; attempt++) {
    let response: Response;
    try {
      response = await send(path, 'GET');
    } catch {
      if (attempt === 0) {
        await new Promise(resolve => setTimeout(resolve, 700));
        continue;
      }
      throw new Error('Нет связи с сервером. Проверьте интернет или смените сеть и попробуйте снова.');
    }
    if (!response.ok) throw new Error(await errorMessage(response));
    return response.json() as Promise<T>;
  }
}

// A channel opened from a buyer's link must work even before the Telegram session is usable.
export async function getPublicChannel(id: number): Promise<Channel> {
  const response = await fetch(API + '/api/public/channels/' + id, { signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
  if (!response.ok) throw new Error('Канал по этой ссылке не найден или сейчас скрыт.');
  const channel = await response.json() as Channel;
  return { ...channel, posts30d: channel.posts30d ?? 0, viewsSamples: channel.viewsSamples ?? 0, likes: channel.likes ?? 0 };
}

export async function uploadOrderImage(orderId: number, image: Blob): Promise<void> {
  const response = await fetch(`${API}/api/orders/${orderId}/image`, {
    method: 'POST',
    credentials: 'include',
    signal: AbortSignal.timeout(UPLOAD_TIMEOUT_MS),
    headers: { 'Content-Type': 'image/jpeg', ...authHeaders() },
    body: image
  });
  if (!response.ok) throw new Error(await errorMessage(response));
}

// An <img> tag cannot send the Telegram header, so protected pictures are fetched first.
export async function getOrderImageUrl(orderId: number): Promise<string> {
  const response = await send(`/api/orders/${orderId}/image`, 'GET');
  if (!response.ok) throw new Error(await errorMessage(response));
  return URL.createObjectURL(await response.blob());
}

export const avatarUrl = (channelId: number) => API + '/api/public/channels/' + channelId + '/avatar';
