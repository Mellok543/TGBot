/** The API returns UTC timestamps, often without a zone marker. */
export const parseUtc = (value: string) => new Date(/(Z|[+-]\d\d:\d\d)$/.test(value) ? value : value + 'Z');

export const money = (value: number) =>
  new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB', maximumFractionDigits: 0 }).format(value);

export const count = (value: number | null) => value === null ? 'Нет данных' : value.toLocaleString('ru-RU');

export const dateTime = (value: string) => parseUtc(value).toLocaleString('ru-RU', { day: 'numeric', month: 'long', hour: '2-digit', minute: '2-digit' });

export const date = (value: string) => parseUtc(value).toLocaleDateString('ru-RU');

export function duration(hours: number | null): string {
  if (hours === null) return 'Не выбран';
  if (hours === 0) return 'Навсегда';
  if (hours % 24 === 0) return hours / 24 + ' дн.';
  return hours + ' ч';
}

export const cpm = (price: number, views: number | null) => views && views > 0 ? money(price * 1000 / views) : 'Нет данных';

export const formatCard = (card: string) => card.replace(/(.{4})/g, '$1 ').trim();

export const errorText = (error: unknown, fallback: string) => error instanceof Error ? error.message : fallback;

export const blockText = (until: string | null | undefined) => until ? 'Заблокирован до ' + date(until) : 'Заблокирован навсегда';
