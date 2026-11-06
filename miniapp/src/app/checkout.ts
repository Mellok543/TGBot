import { post } from './api';
import { openExternal, webApp } from './telegram';

/** Opens PayAnyWay for one of Reklio's own services. The server activates it after a signed payment notice. */
export async function openCheckout(path: string): Promise<void> {
  const order = await post<{ paymentUrl: string }>(path);
  if (!order.paymentUrl.startsWith('https://')) throw new Error('Некорректная ссылка оплаты');
  // A browser blocks a new window opened after a request, so the website navigates instead.
  if (webApp()?.initData) openExternal(order.paymentUrl);
  else window.location.assign(order.paymentUrl);
}

export const CHECKOUT_OPENED = 'Открыта страница оплаты. После оплаты услуга включится сама, обычно в течение минуты.';
