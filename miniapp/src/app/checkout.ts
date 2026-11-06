import { post } from './api';
import { openExternal } from './telegram';

/** Opens PayAnyWay for one of Reklio's own services. The server activates it after a signed payment notice. */
export async function openCheckout(path: string): Promise<void> {
  const order = await post<{ paymentUrl: string }>(path);
  if (!order.paymentUrl.startsWith('https://')) throw new Error('Некорректная ссылка оплаты');
  openExternal(order.paymentUrl);
}

export const CHECKOUT_OPENED = 'Открыта страница оплаты. После оплаты услуга включится сама, обычно в течение минуты.';
